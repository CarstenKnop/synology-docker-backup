using System.Text.Json.Serialization;

namespace SshDockerBackup.Core.Models;

public enum ArchiveKind
{
    Volume,
    Bind,
    Image,

    /// <summary>
    /// A host folder captured because you asked for it, not because a container mounts it. This is
    /// how project directories get saved: the compose file, Dockerfile, .env and scripts usually
    /// sit beside the mounted subfolder and are mounted into nothing.
    /// </summary>
    HostPath,
}

/// <summary>One archived item inside a backup set.</summary>
public sealed class ArchiveEntry
{
    public ArchiveKind Kind { get; set; }
    /// <summary>Volume name, image tag, or the host path for a bind mount.</summary>
    public string Source { get; set; } = "";
    /// <summary>Where this was mounted inside the container (empty for images).</summary>
    public string Destination { get; set; } = "";
    /// <summary>Path of the archive relative to the backup root.</summary>
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }

    /// <summary>
    /// False for a bind mount of a single file, such as /etc/localtime. The distinction matters on
    /// restore: the archive holds the file, so it must be unpacked into the parent directory.
    /// </summary>
    public bool IsDirectory { get; set; } = true;

    /// <summary>Set when the item failed; the backup continues so one bad mount cannot sink the run.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Set when the item was deliberately not archived — the source no longer exists, or it is a
    /// socket or kernel path that cannot meaningfully be captured. Not a failure.
    /// </summary>
    public string? SkipReason { get; set; }

    [JsonIgnore]
    public bool Succeeded => Error is null && SkipReason is null;

    [JsonIgnore]
    public bool WasSkipped => SkipReason is not null;
}

public sealed class NetworkSubnet
{
    public string Subnet { get; set; } = "";
    public string? Gateway { get; set; }
    public string? IpRange { get; set; }
}

/// <summary>
/// A user-defined network as it actually was, not just its name. Recreating one with default
/// settings gives it whatever subnet Docker picks next, which quietly breaks anything pinned to an
/// address — a container told to trust a proxy at 172.20.0.3, for instance.
/// </summary>
public sealed class NetworkDefinition
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "bridge";
    public bool Internal { get; set; }
    public bool Attachable { get; set; }
    public bool EnableIPv6 { get; set; }

    public List<NetworkSubnet> Subnets { get; set; } = [];
    public Dictionary<string, string> Options { get; set; } = [];
    public Dictionary<string, string> Labels { get; set; } = [];
}

public sealed class ContainerBackup
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Image { get; set; } = "";
    public string InspectRelativePath { get; set; } = "";

    /// <summary>The compose file this tool generated. A fallback, not the original.</summary>
    public string? ComposeRelativePath { get; set; }

    /// <summary>
    /// Compose project this container belonged to. Restoring under a different project name leaves
    /// the container running but orphaned: Synology's Project view lists it as having no
    /// containers, because that view keys off the com.docker.compose.project label.
    /// </summary>
    public string? ComposeProject { get; set; }

    /// <summary>Service name within that project, so a single container can be recreated on its own.</summary>
    public string? ComposeService { get; set; }

    /// <summary>Directory the project was defined in, e.g. /volume1/docker/myapp.</summary>
    public string? ComposeWorkingDir { get; set; }

    /// <summary>
    /// The project's own compose file on the host. Preferred over the generated one when it is
    /// still there: it is the real definition rather than a reconstruction from inspect.
    /// </summary>
    public string? ComposeConfigFile { get; set; }
    public bool WasRunning { get; set; }

    /// <summary>
    /// User-defined networks the container was attached to. Recorded so restore can recreate them
    /// before compose runs: a container brought up on a fresh compose-default network starts
    /// cleanly but is invisible to the peers that used to reach it by name.
    /// </summary>
    public List<string> Networks { get; set; } = [];

    public List<ArchiveEntry> Archives { get; set; } = [];
}

/// <summary>Index of a backup set. Written last, so its presence means the run completed.</summary>
public sealed class BackupManifest
{
    public const string FileName = "manifest.json";
    public int FormatVersion { get; set; } = 1;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string HostDescription { get; set; } = "";
    public string DockerRootDir { get; set; } = "";
    public string DockerVersion { get; set; } = "";
    public string AppVersion { get; set; } = "";

    /// <summary>True when this set was written to a folder on the Docker host, e.g. a USB drive.</summary>
    public bool StoredOnHost { get; set; }

    public List<ContainerBackup> Containers { get; set; } = [];

    /// <summary>Host folders captured independently of any container, such as project directories.</summary>
    public List<ArchiveEntry> ExtraPaths { get; set; } = [];

    /// <summary>
    /// Definitions of the user-defined networks in use, so a restore onto a fresh host can rebuild
    /// them with their real addressing rather than whatever Docker would assign.
    /// </summary>
    public List<NetworkDefinition> Networks { get; set; } = [];

    /// <summary>Folder this manifest lives in. Filled in when written or loaded, never serialized.</summary>
    [JsonIgnore]
    public string RootPath { get; set; } = "";

    /// <summary>
    /// Distinct by archive file, not by entry. A folder captured both as a container's bind mount
    /// and as a requested host folder is one file on disk referenced twice, so summing the entries
    /// would report more than the backup actually occupies.
    /// </summary>
    [JsonIgnore]
    public long TotalBytes => AllArchives
        .Where(a => a.Succeeded && a.RelativePath.Length > 0)
        .DistinctBy(a => a.RelativePath, StringComparer.Ordinal)
        .Sum(a => a.SizeBytes);

    [JsonIgnore]
    public IEnumerable<ArchiveEntry> AllArchives =>
        Containers.SelectMany(c => c.Archives).Concat(ExtraPaths);

    [JsonIgnore]
    public IEnumerable<ArchiveEntry> FailedArchives => AllArchives.Where(a => a.Error is not null);

    [JsonIgnore]
    public IEnumerable<ArchiveEntry> SkippedArchives => AllArchives.Where(a => a.WasSkipped);
}
