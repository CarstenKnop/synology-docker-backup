namespace SshDockerBackup.Core.Remote;

/// <summary>A mounted volume on the host: an internal pool, or a USB or eSATA drive.</summary>
public sealed record RemoteVolume(string Path, long TotalBytes, long FreeBytes)
{
    public bool IsRemovable =>
        Path.StartsWith("/volumeUSB", StringComparison.OrdinalIgnoreCase) ||
        Path.StartsWith("/volumeSATA", StringComparison.OrdinalIgnoreCase);

    public string Kind => IsRemovable ? "USB / eSATA" : "Internal pool";
}

public sealed record RemoteEntry(string Name, string FullPath);

/// <summary>
/// File operations on the Docker host, used when the backup lives on the NAS itself rather than on
/// this PC. Everything goes through the same SSH session; there is no SFTP (DSM disables it).
/// </summary>
public interface IRemoteFileSystem
{
    /// <summary>Mounted volumes, so USB drives can be offered as backup targets.</summary>
    Task<IReadOnlyList<RemoteVolume>> ListVolumesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RemoteEntry>> ListDirectoriesAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<RemoteEntry>> ListFilesAsync(string path, CancellationToken ct = default);

    Task CreateDirectoryAsync(string path, CancellationToken ct = default);
    Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default);
    Task<bool> FileExistsAsync(string path, CancellationToken ct = default);

    Task<long> GetFreeBytesAsync(string path, CancellationToken ct = default);
    Task<long> GetFileSizeAsync(string path, CancellationToken ct = default);

    /// <summary>Filesystem a path sits on, used to spot "backing up a volume onto itself".</summary>
    Task<string> GetMountPointAsync(string path, CancellationToken ct = default);

    Task<string?> ComputeSha256Async(string path, CancellationToken ct = default);

    Task WriteTextAsync(string path, string content, CancellationToken ct = default);
    Task<string> ReadTextAsync(string path, CancellationToken ct = default);

    /// <summary>Joins with forward slashes, whatever this PC's separator happens to be.</summary>
    static string Combine(string basePath, params string[] parts) =>
        string.Join('/', new[] { basePath.TrimEnd('/') }
            .Concat(parts.Select(p => p.Trim('/')))
            .Where(p => p.Length > 0));
}
