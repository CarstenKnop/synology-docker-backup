namespace SshDockerBackup.Core.Models;

public enum ImageBackupMode
{
    /// <summary>Never export images. Smallest backups, but a locally built image is unrecoverable.</summary>
    None,

    /// <summary>
    /// Export only images that carry no registry digest. Those were built on the host or came from
    /// a registry that no longer has them, so nothing can pull them back.
    /// </summary>
    LocalOnly,

    /// <summary>Export every image, including ones a registry could supply again.</summary>
    All,
}

public sealed class BackupOptions
{
    /// <summary>Folder that will receive a new timestamped backup set.</summary>
    public string DestinationRoot { get; set; } = "";

    /// <summary>
    /// When true, DestinationRoot is a path on the Docker host (a USB drive or a share) rather than
    /// on this PC. tar then writes straight to that disk instead of streaming over SSH.
    /// </summary>
    public bool DestinationOnHost { get; set; }

    /// <summary>
    /// Stop each container before archiving its data and restart it after. Strongly recommended:
    /// a tar of a live database is very often a corrupt database.
    /// </summary>
    public bool StopContainersDuringBackup { get; set; } = true;

    /// <summary>Archive named volumes and bind mounts. This is the part that actually matters.</summary>
    public bool IncludeVolumes { get; set; } = true;

    /// <summary>
    /// Which images to `docker save`. Defaults to All so a backup is self-contained.
    ///
    /// Re-pulling looks like the thrifty choice until you need it. Most images are tagged :latest,
    /// which is a moving pointer rather than a version — restoring a year later fetches different
    /// software, and a database whose data directory was written by an older major version will
    /// simply refuse to start. Capturing the image pins what actually ran, and removes any
    /// dependency on the host having internet when you restore.
    /// </summary>
    public ImageBackupMode ImageMode { get; set; } = ImageBackupMode.All;

    /// <summary>
    /// Look for project folders near the containers being backed up and include them automatically.
    /// A compose file or .env is mounted into nothing, so following mounts alone never finds it,
    /// and a backup without it cannot rebuild the project.
    /// </summary>
    public bool AutoDiscoverProjectFolders { get; set; } = true;

    /// <summary>Compute a SHA-256 while streaming, so restores can be verified.</summary>
    public bool ComputeChecksums { get; set; } = true;

    /// <summary>
    /// Absolute host folders to archive regardless of whether any container mounts them. Project
    /// directories belong here: a compose file, Dockerfile, .env or helper script is mounted into
    /// nothing, so mount-based backup never sees it.
    /// </summary>
    public List<string> ExtraPaths { get; set; } = [];

    /// <summary>
    /// Host paths to leave out, even when a container mounts them. A media library bind-mounted
    /// into an app is the usual case: it is huge, it is not container data, and it almost certainly
    /// belongs in Hyper Backup instead. Matching covers the path and everything beneath it.
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = [];

    /// <summary>True when the path, or a parent of it, has been excluded.</summary>
    public bool IsExcluded(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath)) return false;

        var candidate = hostPath.TrimEnd('/');

        return ExcludedPaths
            .Select(p => p.Trim().TrimEnd('/'))
            .Where(p => p.Length > 0)
            .Any(p => string.Equals(candidate, p, StringComparison.Ordinal) ||
                      candidate.StartsWith(p + "/", StringComparison.Ordinal));
    }
}

public sealed class RestoreOptions
{
    public string BackupRoot { get; set; } = "";

    /// <summary>True when BackupRoot is a folder on the host rather than on this PC.</summary>
    public bool SourceOnHost { get; set; }

    public bool RestoreVolumeData { get; set; } = true;

    /// <summary>Put archived host folders (project directories) back at their original paths.</summary>
    public bool RestoreExtraPaths { get; set; } = true;

    public bool RecreateContainers { get; set; } = true;
    /// <summary>
    /// Load archived images from the backup rather than pulling them. On by default: if the backup
    /// carries the image, that is the exact one that was running, and using it needs no internet.
    /// </summary>
    public bool LoadImages { get; set; } = true;
    /// <summary>Verify SHA-256 before pushing an archive back to the host.</summary>
    public bool VerifyChecksums { get; set; } = true;

    /// <summary>
    /// Destructive: `docker rm -f` a container that already has the target name so it can be
    /// recreated. Off by default; without it, existing containers are reported and skipped.
    /// </summary>
    public bool RemoveExistingContainers { get; set; }

    /// <summary>
    /// Bring each container back to the run state it had when the backup was taken: one that was
    /// stopped is created but left stopped. Turn this off to start everything, which is what you
    /// usually want when seeding a brand new host.
    /// </summary>
    public bool PreserveRunningState { get; set; } = true;

    /// <summary>
    /// After recreating containers, re-register their project with Synology's Container Manager if
    /// it has no entry. Without this a restored project runs correctly but never appears under the
    /// Project tab, because that list is DSM's own registry rather than a view over Docker.
    /// Uses an undocumented DSM API; failures are logged and never fail the restore.
    /// </summary>
    public bool RegisterSynologyProjects { get; set; } = true;
}
