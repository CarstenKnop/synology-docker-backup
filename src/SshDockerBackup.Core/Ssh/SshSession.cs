using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using SshDockerBackup.Core.Models;

namespace SshDockerBackup.Core.Ssh;

/// <summary>
/// SSH.NET wrapper tuned for this job. Two things drive the design:
///
///   1. Synology disables the SFTP subsystem, so file transfer cannot use SCP or SFTP. Everything
///      moves as a byte stream through a plain exec channel (tar czf - out, tar xzf - in).
///   2. The sudo password is written to the command's stdin, never interpolated into the command
///      line, so it cannot appear in ps output, in shell history, or in our own logs.
/// </summary>
public sealed class SshSession(ILogger<SshSession> log) : ISshSession
{
    private const int BufferSize = 512 * 1024;

    private SshClient? _client;
    private string? _password;

    public bool IsConnected => _client?.IsConnected == true;
    public ConnectionSettings? Settings { get; private set; }

    /// <summary>
    /// sudo on DSM ships a restricted secure_path that omits /usr/local/bin and /usr/sbin, which is
    /// why "sudo visudo" reports command not found there. docker lives in /usr/local/bin, so every
    /// command sets its own PATH inside the remote shell instead of trusting sudo's.
    /// </summary>
    private const string PathPrefix = "PATH=/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:$PATH; export PATH; ";

    /// <summary>Wraps a command for /bin/sh so pipes and redirects work, optionally under sudo.</summary>
    public static string BuildCommand(string command, bool elevate) =>
        elevate
            ? "sudo -S -p '' /bin/sh -c " + Quote(PathPrefix + command)
            : "/bin/sh -c " + Quote(PathPrefix + command);

    /// <summary>POSIX single-quoting: safe for paths containing spaces, quotes or dollar signs.</summary>
    public static string Quote(string value)
    {
        const string escaped = @"'\''";
        return "'" + value.Replace("'", escaped, StringComparison.Ordinal) + "'";
    }

    public async Task ConnectAsync(ConnectionSettings settings, HostKeyApproval? approve, CancellationToken ct = default)
    {
        Disconnect();

        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(settings.PrivateKeyPath) && File.Exists(settings.PrivateKeyPath))
        {
            log.LogInformation("Adding private key auth from {Path}", settings.PrivateKeyPath);
            var keyFile = new PrivateKeyFile(settings.PrivateKeyPath, settings.Password ?? "");
            methods.Add(new PrivateKeyAuthenticationMethod(settings.Username, keyFile));
        }

        if (!string.IsNullOrEmpty(settings.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(settings.Username, settings.Password));

            // DSM negotiates keyboard-interactive in some configurations; answer its prompts with
            // the same password rather than letting the connection fail.
            var keyboard = new KeyboardInteractiveAuthenticationMethod(settings.Username);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    log.LogDebug("Keyboard-interactive prompt: {Prompt}", prompt.Request);
                    prompt.Response = settings.Password;
                }
            };
            methods.Add(keyboard);
        }

        if (methods.Count == 0)
            throw new InvalidOperationException("No password and no usable private key were supplied.");

        var info = new ConnectionInfo(settings.Host, settings.Port, settings.Username, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds),
        };

        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };

        Exception? rejection = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = e.FingerPrintSHA256;

            if (string.Equals(fingerprint, settings.KnownHostFingerprint, StringComparison.Ordinal))
            {
                e.CanTrust = true;
                return;
            }

            if (settings.KnownHostFingerprint is { Length: > 0 } known)
            {
                // A changed key is worth stopping for: it is what a machine-in-the-middle looks like.
                e.CanTrust = false;
                rejection = new SshCommandException(
                    $"Host key for {settings.Host} has changed.{Environment.NewLine}" +
                    $"Expected SHA256:{known}{Environment.NewLine}" +
                    $"Received SHA256:{fingerprint}{Environment.NewLine}" +
                    "Clear the saved fingerprint in the Connection tab only if you know why it changed.");
                return;
            }

            // Trust on first use, but let the UI show the fingerprint and make the call.
            var accepted = approve is null || approve(fingerprint, e.HostKeyName).GetAwaiter().GetResult();
            e.CanTrust = accepted;

            if (accepted) settings.KnownHostFingerprint = fingerprint;
            else rejection = new SshCommandException("The host key was not accepted.");
        };

        log.LogInformation("Connecting to {Target}", settings);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch when (rejection is not null)
        {
            client.Dispose();
            throw rejection;
        }
        catch
        {
            client.Dispose();
            throw;
        }

        if (rejection is not null)
        {
            client.Dispose();
            throw rejection;
        }

        _client = client;
        _password = settings.Password;
        Settings = settings;
        log.LogInformation("Connected to {Target}, server {Server}", settings, info.ServerVersion);
    }

    public void Disconnect()
    {
        if (_client is null) return;

        try
        {
            if (_client.IsConnected) _client.Disconnect();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Ignoring error while disconnecting");
        }

        _client.Dispose();
        _client = null;
        _password = null;
        Settings = null;
    }

    /// <summary>
    /// Raised when the session is gone and could not be re-established, so the UI can stop claiming
    /// it is connected.
    /// </summary>
    public event Action? ConnectionLost;

    private readonly SemaphoreSlim _reconnectGate = new(1, 1);

    /// <summary>
    /// An idle session is dropped by the server long before the user notices — the app can sit for
    /// hours between a backup and a restore. Rather than failing the operation, reconnect with the
    /// credentials already held for this session and carry on.
    /// </summary>
    private async Task<SshClient> EnsureClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true } live) return live;

        if (_client is null || Settings is null)
        {
            throw new InvalidOperationException(
                "Not connected to the Docker host. Connect on the Connection tab first.");
        }

        await _reconnectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have reconnected while we waited for the gate.
            if (_client is { IsConnected: true } reconnected) return reconnected;

            log.LogWarning("SSH session to {Target} had dropped; reconnecting", Settings);

            try
            {
                await _client.ConnectAsync(ct).ConfigureAwait(false);
                log.LogInformation("Reconnected to {Target}", Settings);
                return _client;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not reconnect to {Target}", Settings);
                ConnectionLost?.Invoke();

                throw new SshCommandException(
                    $"The SSH session to {Settings} dropped and could not be re-established: " +
                    $"{ex.Message}{Environment.NewLine}Reconnect on the Connection tab and try again.");
            }
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    private SshCommand CreateCommand(SshClient client, string command, bool elevate)
    {
        var full = BuildCommand(command, elevate);

        // Safe to log: the password only ever travels over stdin, never in this string.
        log.LogDebug("exec: {Command}", full);

        var cmd = client.CreateCommand(full);
        cmd.CommandTimeout = TimeSpan.FromMinutes(Settings?.CommandTimeoutMinutes ?? 240);
        return cmd;
    }

    /// <summary>Feeds sudo its password, then signals EOF so the remote command sees a closed stdin.</summary>
    private void WritePasswordAndCloseStdin(SshCommand cmd, bool elevate)
    {
        var input = cmd.CreateInputStream();
        try
        {
            if (elevate)
            {
                var bytes = Encoding.UTF8.GetBytes((_password ?? "") + "\n");
                input.Write(bytes, 0, bytes.Length);
                input.Flush();
            }
        }
        finally
        {
            input.Dispose();
        }
    }

    public async Task<CommandResult> RunAsync(string command, bool elevate, CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        using var cmd = CreateCommand(client, command, elevate);
        var async = cmd.BeginExecute();

        WritePasswordAndCloseStdin(cmd, elevate);

        var stdErrTask = Task.Run(() => ReadAllText(cmd.ExtendedOutputStream), CancellationToken.None);
        var stdOut = await Task.Run(() => ReadAllText(cmd.OutputStream), ct).ConfigureAwait(false);

        cmd.EndExecute(async);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        var exitCode = cmd.ExitStatus ?? -1;
        if (exitCode != 0)
            log.LogWarning("Command exited {Exit}: {Error}", exitCode, stdErr.Trim());

        return new CommandResult(exitCode, stdOut, CleanSudoNoise(stdErr));
    }

    public async Task<TransferResult> DownloadAsync(string command, Stream destination, bool elevate,
        bool hash, IProgress<long>? progress, CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        using var cmd = CreateCommand(client, command, elevate);
        using var sha = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        var async = cmd.BeginExecute();
        WritePasswordAndCloseStdin(cmd, elevate);

        var stdErrTask = Task.Run(() => ReadAllText(cmd.ExtendedOutputStream), CancellationToken.None);

        // A stalled remote command would leave the read blocking, so cancellation tears the channel
        // down rather than only being noticed between buffers.
        await using var abort = ct.Register(() =>
        {
            try { cmd.CancelAsync(false, 500); } catch { /* best effort */ }
        });

        var buffer = new byte[BufferSize];
        long total = 0;

        try
        {
            while (true)
            {
                var read = await cmd.OutputStream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha?.AppendData(buffer, 0, read);
                total += read;
                progress?.Report(total);
            }
        }
        catch (ObjectDisposedException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        cmd.EndExecute(async);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        var digest = sha is null ? null : Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        return new TransferResult(total, digest, cmd.ExitStatus ?? -1, CleanSudoNoise(stdErr));
    }

    public async Task<TransferResult> UploadAsync(string command, Stream source, bool elevate,
        IProgress<long>? progress, CancellationToken ct = default)
    {
        var client = await EnsureClientAsync(ct).ConfigureAwait(false);
        using var cmd = CreateCommand(client, command, elevate);

        var async = cmd.BeginExecute();
        var stdErrTask = Task.Run(() => ReadAllText(cmd.ExtendedOutputStream), CancellationToken.None);
        var stdOutTask = Task.Run(() => ReadAllText(cmd.OutputStream), CancellationToken.None);

        long total = 0;
        var input = cmd.CreateInputStream();

        try
        {
            if (elevate)
            {
                // sudo -S consumes exactly one line from stdin and then execs the command, which
                // goes on reading the same descriptor. So the archive simply follows the password.
                var pw = Encoding.UTF8.GetBytes((_password ?? "") + "\n");
                await input.WriteAsync(pw, ct).ConfigureAwait(false);
                await input.FlushAsync(ct).ConfigureAwait(false);
            }

            var buffer = new byte[BufferSize];
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;

                await input.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }

            await input.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            input.Dispose();
        }

        cmd.EndExecute(async);
        await stdOutTask.ConfigureAwait(false);
        var stdErr = await stdErrTask.ConfigureAwait(false);

        return new TransferResult(total, null, cmd.ExitStatus ?? -1, CleanSudoNoise(stdErr));
    }

    private static string ReadAllText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 16 * 1024, leaveOpen: true);
        return reader.ReadToEnd();
    }

    /// <summary>Strips sudo's own chatter so that real errors stand out in the log.</summary>
    private static string CleanSudoNoise(string stdErr)
    {
        if (string.IsNullOrEmpty(stdErr)) return "";

        var lines = stdErr.Split('\n')
            .Where(line => !line.StartsWith("[sudo] password", StringComparison.Ordinal));

        return string.Join('\n', lines).Trim();
    }

    public void Dispose() => Disconnect();
}
