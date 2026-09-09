using SshDockerBackup.Core.Models;

namespace SshDockerBackup.Core.Ssh;

/// <summary>Asks the caller whether an unrecognised host key should be trusted.</summary>
public delegate Task<bool> HostKeyApproval(string sha256Fingerprint, string keyAlgorithm);

public interface ISshSession : IDisposable
{
    bool IsConnected { get; }
    ConnectionSettings? Settings { get; }

    Task ConnectAsync(ConnectionSettings settings, HostKeyApproval? approve, CancellationToken ct = default);
    void Disconnect();

    /// <summary>Runs a command and buffers its output as text.</summary>
    Task<CommandResult> RunAsync(string command, bool elevate, CancellationToken ct = default);

    /// <summary>Streams the command's stdout into <paramref name="destination"/> without buffering it in memory.</summary>
    Task<TransferResult> DownloadAsync(string command, Stream destination, bool elevate,
        bool hash, IProgress<long>? progress, CancellationToken ct = default);

    /// <summary>Streams <paramref name="source"/> into the command's stdin.</summary>
    Task<TransferResult> UploadAsync(string command, Stream source, bool elevate,
        IProgress<long>? progress, CancellationToken ct = default);
}
