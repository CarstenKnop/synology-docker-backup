using SshDockerBackup.Core.Models;
using SshDockerBackup.Core.Ssh;

namespace SshDockerBackup.Core.Docker;

public sealed record DockerHostInfo(string Version, string RootDir, string HostName);

public enum RemotePathKind
{
    /// <summary>Nothing at that path. Common for containers whose data was moved or deleted.</summary>
    Missing,
    Directory,
    /// <summary>A regular file, or a symlink to one. Bind mounts of single files are ordinary.</summary>
    File,
    /// <summary>Socket, device or FIFO — exists, but there is nothing meaningful to archive.</summary>
    Special,
}

public interface IDockerService
{
    /// <summary>Verifies docker is reachable and that sudo (if enabled) actually works.</summary>
    Task<DockerHostInfo> ProbeAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default);

    Task<string> InspectAsync(string containerId, CancellationToken ct = default);

    /// <summary>Absolute host path backing a named volume.</summary>
    Task<string?> GetVolumeMountpointAsync(string volumeName, CancellationToken ct = default);

    Task<long> GetPathSizeBytesAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// True when the image carries a registry digest and so could be pulled again, false when it
    /// exists only on this host, null when it could not be inspected at all.
    /// </summary>
    Task<bool?> IsImagePullableAsync(string imageTag, CancellationToken ct = default);

    /// <summary>
    /// The image's own content ID, which is what identifies it. Two containers can name the same
    /// image differently — "postgres" and "postgres:latest" are one image — so the reference is not
    /// a safe key for "have I already saved this?". Null when the image is not present locally.
    /// </summary>
    Task<string?> GetImageIdAsync(string imageTag, CancellationToken ct = default);

    Task StopContainerAsync(string idOrName, CancellationToken ct = default);
    Task StartContainerAsync(string idOrName, CancellationToken ct = default);

    Task<bool> PathExistsAsync(string path, CancellationToken ct = default);

    /// <summary>What is actually at that path, so archiving can be shaped correctly or skipped.</summary>
    Task<RemotePathKind> InspectPathAsync(string path, CancellationToken ct = default);

    /// <summary>Classifies many paths in one round trip. Used by the pre-flight check.</summary>
    Task<IReadOnlyDictionary<string, RemotePathKind>> InspectPathsAsync(
        IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>Measures many paths in one round trip. du walks each tree, so this is not instant.</summary>
    Task<IReadOnlyDictionary<string, long>> GetPathSizesAsync(
        IEnumerable<string> paths, CancellationToken ct = default);

    /// <summary>
    /// Walks up from each bind-mount source looking for folders that hold a compose file or a
    /// Dockerfile. Those are project directories, which are mounted into nothing and are therefore
    /// invisible to a backup that only follows mounts.
    /// </summary>
    Task<IReadOnlyList<string>> FindProjectFoldersAsync(
        IEnumerable<string> bindPaths, CancellationToken ct = default);

    /// <summary>Full definition of a user-defined network, or null if it cannot be inspected.</summary>
    Task<NetworkDefinition?> InspectNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>Of the images given, the ones the host does not already have.</summary>
    Task<IReadOnlyList<string>> FindMissingImagesAsync(
        IEnumerable<string> images, CancellationToken ct = default);

    /// <summary>
    /// Whether the host can reach a container registry. Null when it could not be determined —
    /// which is not the same as offline, and must not be reported as such.
    /// </summary>
    Task<bool?> CanReachRegistryAsync(CancellationToken ct = default);

    Task<bool> VolumeExistsAsync(string volumeName, CancellationToken ct = default);
    Task CreateVolumeAsync(string volumeName, CancellationToken ct = default);
    Task EnsureDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Command that writes a gzipped tar of <paramref name="hostPath"/> to stdout. A directory is
    /// archived by its contents; a single file by name from its parent, since tar cannot chdir
    /// into a file.
    /// </summary>
    string BuildArchiveCommand(string hostPath, bool isDirectory = true);

    /// <summary>Command that reads a gzipped tar from stdin and unpacks it into <paramref name="hostPath"/>.</summary>
    string BuildExtractCommand(string hostPath);

    /// <summary>Unpacks an archive that is already on the host, with no stream over SSH.</summary>
    string BuildExtractFromFileCommand(string archivePath, string hostPath);

    string BuildImageSaveCommand(string imageTag);
    string BuildImageLoadCommand();
    string BuildImageLoadFromFileCommand(string archivePath);

    /// <summary>Runs an arbitrary docker subcommand, e.g. "compose up -d".</summary>
    Task<CommandResult> RunDockerAsync(string arguments, CancellationToken ct = default);
}
