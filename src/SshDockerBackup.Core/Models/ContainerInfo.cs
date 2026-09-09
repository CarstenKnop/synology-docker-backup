namespace SshDockerBackup.Core.Models;

public enum MountKind { Volume, Bind, Tmpfs, Other }

/// <summary>One mount attached to a container. This is what actually holds the data.</summary>
public sealed class MountInfo
{
    public MountKind Kind { get; init; }
    /// <summary>Named-volume name, or empty for bind mounts.</summary>
    public string Name { get; init; } = "";
    /// <summary>Absolute path on the Docker host.</summary>
    public string Source { get; init; } = "";
    /// <summary>Path inside the container.</summary>
    public string Destination { get; init; } = "";
    public bool ReadWrite { get; init; }

    /// <summary>tmpfs holds nothing durable, so there is nothing worth archiving.</summary>
    public bool IsBackupable => Kind is MountKind.Volume or MountKind.Bind
                                && !string.IsNullOrWhiteSpace(Source);

    public override string ToString() =>
        Kind == MountKind.Volume ? $"volume {Name} -> {Destination}" : $"bind {Source} -> {Destination}";
}

public sealed class ContainerInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public string State { get; init; } = "";
    public string Status { get; init; } = "";
    public string Ports { get; init; } = "";
    public IReadOnlyList<MountInfo> Mounts { get; set; } = [];

    /// <summary>Raw `docker inspect` output. Archived verbatim; it is the definitive rebuild recipe.</summary>
    public string InspectJson { get; set; } = "";

    /// <summary>Compose project this container belongs to, if it was created by compose.</summary>
    public string? ComposeProject { get; set; }

    /// <summary>
    /// The project's compose file on the host. Its folder is worth backing up and is easy to miss:
    /// it is mounted into nothing, so following mounts never reaches it.
    /// </summary>
    public string? ComposeConfigFile { get; set; }

    public string ShortId => Id.Length > 12 ? Id[..12] : Id;
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Name} ({Image})";
}
