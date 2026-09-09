using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using SshDockerBackup.Core.Ssh;

namespace SshDockerBackup.Core.Remote;

public sealed class RemoteFileSystem(ISshSession ssh, ILogger<RemoteFileSystem> log) : IRemoteFileSystem
{
    private bool Elevate => ssh.Settings?.UseSudo ?? true;

    public async Task<IReadOnlyList<RemoteVolume>> ListVolumesAsync(CancellationToken ct = default)
    {
        // Two sources, unioned, because neither alone is reliable on DSM:
        //
        //   1. df mount points under /volume. Catches internal pools, and USB shares on the DSM
        //      versions that mount them at /volumeUSB1/usbshare1-1.
        //   2. A direct listing of /volumeUSB*/* and /volumeSATA*/*. Catches the case where df
        //      reports the parent rather than the share, which is what File Station shows you
        //      as "usbshare1-1", "usbshare2" and so on.
        //
        // Then df each surviving path once for its size, so this stays a single round trip.
        const string script = """
            {
              df -kP 2>/dev/null | awk 'NR>1 && $6 ~ /^\/volume/ { print $6 }'
              for d in /volumeUSB*/* /volumeSATA*/* /volumeUSB* /volumeSATA*; do
                [ -d "$d" ] && printf '%s\n' "$d"
              done
            } | sort -u | while IFS= read -r p; do
              [ -d "$p" ] || continue
              df -kP "$p" 2>/dev/null | awk -v P="$p" 'NR==2 { print P "\t" $2 "\t" $4 "\t" $6 }'
            done
            """;

        var result = await ssh.RunAsync(script, Elevate, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            log.LogWarning("Could not list volumes: {Error}", result.StdErr);
            return [];
        }

        var volumes = new List<RemoteVolume>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            var path = parts[0].Trim();
            var resolvedMount = parts[3].Trim();

            // A bare /volumeUSB1 with nothing mounted on it is just a directory on the root
            // filesystem, and would otherwise show up advertising the system disk's free space.
            if (resolvedMount == "/" && path != "/") continue;

            if (!long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalKb) ||
                !long.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var freeKb))
                continue;

            volumes.Add(new RemoteVolume(path, totalKb * 1024, freeKb * 1024));
        }

        log.LogInformation("Found {Count} volumes: {Paths}", volumes.Count,
            string.Join(", ", volumes.Select(v => v.Path)));

        return volumes;
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListDirectoriesAsync(string path, CancellationToken ct = default) =>
        await ListAsync(path, directories: true, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<RemoteEntry>> ListFilesAsync(string path, CancellationToken ct = default) =>
        await ListAsync(path, directories: false, ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, bool directories, CancellationToken ct)
    {
        var test = directories ? "-d" : "-f";

        // Skips DSM's own @eaDir/@tmp clutter and dotfiles, which are never useful here.
        var script = $$"""
            cd {{SshSession.Quote(path)}} 2>/dev/null || exit 1
            ls -1A 2>/dev/null | while IFS= read -r e; do
              case "$e" in @*|.*) continue;; esac
              if [ {{test}} "$e" ]; then printf '%s\n' "$e"; fi
            done
            """;

        var result = await ssh.RunAsync(script, Elevate, ct).ConfigureAwait(false);
        if (!result.Success) return [];

        return
        [
            .. result.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.TrimEnd('\r'))
                .Where(name => name.Length > 0)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new RemoteEntry(name, IRemoteFileSystem.Combine(path, name)))
        ];
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync($"mkdir -p {SshSession.Quote(path)}", Elevate, ct).ConfigureAwait(false);
        result.EnsureSuccess($"mkdir -p {path}");
        log.LogInformation("Created remote folder {Path}", path);
    }

    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default) =>
        await TestAsync("-d", path, ct).ConfigureAwait(false);

    public async Task<bool> FileExistsAsync(string path, CancellationToken ct = default) =>
        await TestAsync("-f", path, ct).ConfigureAwait(false);

    private async Task<bool> TestAsync(string test, string path, CancellationToken ct)
    {
        var result = await ssh.RunAsync(
            $"[ {test} {SshSession.Quote(path)} ] && echo yes || echo no", Elevate, ct).ConfigureAwait(false);
        return result.StdOut.Trim() == "yes";
    }

    public async Task<long> GetFreeBytesAsync(string path, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(
            $"df -kP {SshSession.Quote(path)} 2>/dev/null | awk 'NR==2 {{ print $4 }}'", Elevate, ct)
            .ConfigureAwait(false);

        return long.TryParse(result.StdOut.Trim(), out var kb) ? kb * 1024 : 0;
    }

    public async Task<long> GetFileSizeAsync(string path, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(
            $"stat -c %s {SshSession.Quote(path)} 2>/dev/null", Elevate, ct).ConfigureAwait(false);

        return long.TryParse(result.StdOut.Trim(), out var bytes) ? bytes : 0;
    }

    public async Task<string> GetMountPointAsync(string path, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(
            $"df -P {SshSession.Quote(path)} 2>/dev/null | awk 'NR==2 {{ print $6 }}'", Elevate, ct)
            .ConfigureAwait(false);

        return result.StdOut.Trim();
    }

    public async Task<string?> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(
            $"sha256sum {SshSession.Quote(path)} 2>/dev/null | cut -d' ' -f1", Elevate, ct).ConfigureAwait(false);

        var hash = result.StdOut.Trim();
        return hash.Length == 64 ? hash : null;
    }

    public async Task WriteTextAsync(string path, string content, CancellationToken ct = default)
    {
        // Streams through stdin rather than embedding the text in the command line, so size and
        // quoting stop being a concern.
        using var buffer = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var result = await ssh.UploadAsync(
            $"cat > {SshSession.Quote(path)}", buffer, Elevate, null, ct).ConfigureAwait(false);

        if (!result.Success)
            throw new SshCommandException($"Could not write {path}: {result.StdErr}");
    }

    public async Task<string> ReadTextAsync(string path, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync($"cat {SshSession.Quote(path)}", Elevate, ct).ConfigureAwait(false);
        result.EnsureSuccess($"read {path}");
        return result.StdOut;
    }
}
