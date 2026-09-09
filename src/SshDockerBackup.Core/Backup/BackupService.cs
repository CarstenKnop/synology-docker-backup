using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SshDockerBackup.Core.Docker;
using SshDockerBackup.Core.Models;
using SshDockerBackup.Core.Remote;
using SshDockerBackup.Core.Reporting;
using SshDockerBackup.Core.Ssh;

namespace SshDockerBackup.Core.Backup;

public enum SourceIssueKind
{
    /// <summary>Left out on purpose, because you said so.</summary>
    Excluded,
    /// <summary>The path is gone. Usually a container whose data was moved or deleted.</summary>
    Missing,
    /// <summary>Host plumbing such as /etc/localtime or docker.sock, never container data.</summary>
    SystemPlumbing,
    /// <summary>A socket, device or FIFO: it exists, but there is nothing to capture.</summary>
    Special,
}

public sealed record SourceIssue(
    string Owner, string Source, string Destination, SourceIssueKind Kind, string Reason);

/// <summary>
/// What a backup would take and what it would skip, worked out before the run starts. Reported up
/// front rather than interrupting a long transfer with a dialog nobody is sitting there to answer.
/// </summary>
public sealed record PreflightReport(
    IReadOnlyList<SourceIssue> Issues,
    int SourcesChecked,
    long EstimatedBytes,
    long DestinationFreeBytes,
    string DestinationDescription)
{
    public bool HasIssues => Issues.Count > 0;

    public IEnumerable<SourceIssue> Missing => Issues.Where(i => i.Kind == SourceIssueKind.Missing);

    /// <summary>Free space could not be determined — a UNC destination, typically.</summary>
    public bool FreeSpaceKnown => DestinationFreeBytes > 0;

    /// <summary>
    /// The estimate is uncompressed, so it is an upper bound rather than a prediction: text and
    /// databases shrink a lot, photos and video essentially not at all.
    /// </summary>
    public bool MightNotFit => FreeSpaceKnown && EstimatedBytes > DestinationFreeBytes;
}

public interface IBackupService
{
    /// <summary>
    /// Checks every source a backup would touch and measures it, without transferring anything.
    /// </summary>
    Task<PreflightReport> PreflightAsync(
        IReadOnlyList<ContainerInfo> containers,
        BackupOptions options,
        CancellationToken ct = default);

    Task<BackupManifest> BackupAsync(
        IReadOnlyList<ContainerInfo> containers,
        BackupOptions options,
        IProgress<BackupProgress>? progress,
        CancellationToken ct = default);
}

/// <summary>
/// Captures container data either onto this PC or onto a folder on the Docker host itself, such as
/// a USB drive plugged into the NAS.
///
/// The two destinations use genuinely different mechanics. Writing here means streaming tar's stdout
/// back over the SSH channel; writing on the host means redirecting tar into a file locally, which
/// never touches the network and is far faster for large volumes.
/// </summary>
public sealed class BackupService(
    ISshSession ssh,
    IDockerService docker,
    IRemoteFileSystem remote,
    ILogger<BackupService> log) : IBackupService
{
    /// <summary>Human-readable summary written into every backup set.</summary>
    public const string ReportFileName = "REPORT.txt";

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    /// <summary>How often the host-side file is measured while tar is still writing to it.</summary>
    private static readonly TimeSpan RemotePollInterval = TimeSpan.FromMilliseconds(1500);

    private bool Elevate => ssh.Settings?.UseSudo ?? true;

    private sealed record CaptureResult(long Bytes, string? Sha256, int ExitCode, string StdErr);

    /// <summary>
    /// Host plumbing that containers mount to integrate with the machine, never container data.
    /// Archiving these is pointless, and restoring them would write over the host's own system
    /// files — /etc/localtime would rewrite its timezone, docker.sock is not even a file.
    /// </summary>
    private static readonly string[] SystemPlumbingPaths =
    [
        "/etc/localtime", "/etc/timezone", "/etc/hosts", "/etc/hostname", "/etc/resolv.conf",
        "/var/run/docker.sock", "/run/docker.sock",
    ];

    private static bool IsSystemPlumbing(string path) =>
        SystemPlumbingPaths.Contains(path, StringComparer.Ordinal) ||
        path is "/proc" or "/sys" or "/dev" ||
        path.StartsWith("/proc/", StringComparison.Ordinal) ||
        path.StartsWith("/sys/", StringComparison.Ordinal) ||
        path.StartsWith("/dev/", StringComparison.Ordinal);

    /// <summary>
    /// Decides whether a source is worth archiving and how. tar cannot chdir into a file, so a
    /// single-file bind mount has to be archived by name from its parent instead.
    /// </summary>
    internal static (SourceIssueKind? Issue, string? Reason, bool IsDirectory) Classify(
        string hostPath, RemotePathKind kind, BackupOptions options)
    {
        if (options.IsExcluded(hostPath))
            return (SourceIssueKind.Excluded, "You excluded this path from backups.", true);

        if (IsSystemPlumbing(hostPath))
            return (SourceIssueKind.SystemPlumbing, "Host system path rather than container data.", true);

        return kind switch
        {
            RemotePathKind.Missing =>
                (SourceIssueKind.Missing, "The source no longer exists on the host.", true),
            RemotePathKind.Special =>
                (SourceIssueKind.Special, "Socket, device or FIFO — nothing to archive.", true),
            RemotePathKind.File => (null, null, false),
            _ => (null, null, true),
        };
    }

    private async Task<(string? SkipReason, bool IsDirectory)> ClassifySourceAsync(
        string hostPath, BackupOptions options, CancellationToken ct)
    {
        // Only ask the host when the path is not already ruled out on its name alone.
        if (options.IsExcluded(hostPath) || IsSystemPlumbing(hostPath))
            return (Classify(hostPath, RemotePathKind.Missing, options).Reason, true);

        var kind = await docker.InspectPathAsync(hostPath, ct).ConfigureAwait(false);
        var (_, reason, isDirectory) = Classify(hostPath, kind, options);
        return (reason, isDirectory);
    }

    public async Task<PreflightReport> PreflightAsync(
        IReadOnlyList<ContainerInfo> containers,
        BackupOptions options,
        CancellationToken ct = default)
    {
        var extraPaths = options.ExtraPaths;
        var candidates = new List<(string Owner, string Source, string Destination, string HostPath)>();

        foreach (var container in containers)
        {
            foreach (var mount in container.Mounts.Where(m => m.IsBackupable))
            {
                var hostPath = mount.Source;

                if (mount.Kind == MountKind.Volume)
                {
                    var resolved = await docker.GetVolumeMountpointAsync(mount.Name, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(resolved)) hostPath = resolved;
                }

                if (string.IsNullOrWhiteSpace(hostPath)) continue;

                var label = mount.Kind == MountKind.Volume ? mount.Name : mount.Source;
                candidates.Add((container.Name, label, mount.Destination, hostPath));
            }
        }

        foreach (var path in extraPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
            candidates.Add(("(host folder)", path, "", path));

        // One round trip for every path, rather than one per path.
        var kinds = await docker.InspectPathsAsync(candidates.Select(c => c.HostPath), ct).ConfigureAwait(false);

        var issues = new List<SourceIssue>();
        var archivable = new List<string>();

        foreach (var candidate in candidates)
        {
            var kind = kinds.GetValueOrDefault(candidate.HostPath, RemotePathKind.Missing);
            var (issue, reason, _) = Classify(candidate.HostPath, kind, options);

            if (issue is not null)
                issues.Add(new SourceIssue(candidate.Owner, candidate.Source, candidate.Destination, issue.Value, reason!));
            else
                archivable.Add(candidate.HostPath);
        }

        // Measure only what would actually be archived, and only once per distinct path: a volume
        // shared by two containers is copied once, so counting it twice would overstate the total.
        var sizes = await docker.GetPathSizesAsync(archivable, ct).ConfigureAwait(false);
        var estimated = archivable.Distinct(StringComparer.Ordinal).Sum(p => sizes.GetValueOrDefault(p));

        var (freeBytes, destinationDescription) =
            await MeasureDestinationAsync(options, ct).ConfigureAwait(false);

        log.LogInformation(
            "Pre-flight: {Count} sources, {Issues} skipped, ~{Size} to archive, {Free} free at {Dest}",
            candidates.Count, issues.Count, BackupProgress.FormatBytes(estimated),
            freeBytes > 0 ? BackupProgress.FormatBytes(freeBytes) : "unknown", destinationDescription);

        return new PreflightReport(issues, candidates.Count, estimated, freeBytes, destinationDescription);
    }

    /// <summary>Free space at the destination, or 0 when it cannot be determined.</summary>
    private async Task<(long FreeBytes, string Description)> MeasureDestinationAsync(
        BackupOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.DestinationRoot))
            return (0, "no destination chosen");

        if (options.DestinationOnHost)
        {
            var free = await remote.GetFreeBytesAsync(options.DestinationRoot, ct).ConfigureAwait(false);
            return (free, options.DestinationRoot);
        }

        try
        {
            // DriveInfo cannot answer for a UNC path, which is a normal thing to back up to, so
            // an unknown result has to be reported rather than treated as "no space".
            var root = Path.GetPathRoot(Path.GetFullPath(options.DestinationRoot));
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return (0, options.DestinationRoot);

            return (new DriveInfo(root).AvailableFreeSpace, options.DestinationRoot);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not determine free space at {Path}", options.DestinationRoot);
            return (0, options.DestinationRoot);
        }
    }

    /// <summary>State that lives for one backup run, kept together so it is not threaded through
    /// every method as a growing list of parameters.</summary>
    private sealed class RunContext
    {
        /// <summary>Anything already archived this run, keyed "volume:name" / "bind:path" / "image:tag".</summary>
        public Dictionary<string, ArchiveEntry> Archived { get; } = new(StringComparer.Ordinal);

        /// <summary>Containers we stopped, so they can be restarted even if the run fails.</summary>
        public List<string> StoppedByUs { get; } = [];

        /// <summary>Cached "can a registry supply this again?" verdict per image tag.</summary>
        public Dictionary<string, bool?> ImagePullable { get; } = new(StringComparer.Ordinal);

        /// <summary>Cached image reference to content ID, so each image is inspected once.</summary>
        public Dictionary<string, string?> ImageIds { get; } = new(StringComparer.Ordinal);

        /// <summary>Local-only images we chose not to archive, so the summary can warn about them.</summary>
        public List<string> SkippedLocalImages { get; } = [];
    }

    public async Task<BackupManifest> BackupAsync(
        IReadOnlyList<ContainerInfo> containers,
        BackupOptions options,
        IProgress<BackupProgress>? progress,
        CancellationToken ct = default)
    {
        // A folders-only backup is legitimate: project directories are worth capturing on their own.
        if (containers.Count == 0 && options.ExtraPaths.Count == 0)
            throw new InvalidOperationException("Nothing was selected to back up.");
        if (string.IsNullOrWhiteSpace(options.DestinationRoot))
            throw new InvalidOperationException("No backup destination was chosen.");

        progress?.Report(new BackupProgress("Preparing", "Reading host details"));
        var host = await docker.ProbeAsync(ct).ConfigureAwait(false);

        var folderName = $"{Sanitise(host.HostName)}_{DateTime.Now:yyyyMMdd-HHmmss}";
        var root = options.DestinationOnHost
            ? IRemoteFileSystem.Combine(options.DestinationRoot, folderName)
            : Path.Combine(options.DestinationRoot, folderName);

        if (options.DestinationOnHost)
        {
            await remote.CreateDirectoryAsync(root, ct).ConfigureAwait(false);
            await WarnIfSameVolumeAsync(root, host.RootDir, ct).ConfigureAwait(false);
        }
        else
        {
            Directory.CreateDirectory(root);
        }

        log.LogInformation("Backup root: {Root} ({Where})", root,
            options.DestinationOnHost ? "on the host" : "on this PC");

        var manifest = new BackupManifest
        {
            RootPath = root,
            StoredOnHost = options.DestinationOnHost,
            HostDescription = $"{host.HostName} ({ssh.Settings})",
            DockerRootDir = host.RootDir,
            DockerVersion = host.Version,
            AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        };

        var run = new RunContext();

        try
        {
            for (var index = 0; index < containers.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var container = containers[index];

                progress?.Report(new BackupProgress("Backing up", container.Name,
                    ItemIndex: index, ItemCount: containers.Count));

                var entry = await BackupContainerAsync(
                    container, options, root, run, progress, index, containers.Count, ct)
                    .ConfigureAwait(false);

                manifest.Containers.Add(entry);
            }
        }
        finally
        {
            // Restart whatever we stopped, even if the run failed or was cancelled.
            foreach (var name in run.StoppedByUs)
            {
                try
                {
                    await docker.StartContainerAsync(name, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to restart {Container} after backup", name);
                }
            }
        }

        // Find project folders the user did not have to know about. A compose file or .env sits
        // beside the mounted subfolder and is mounted into nothing, so a backup that only follows
        // mounts silently omits the very thing needed to rebuild the project.
        if (options.AutoDiscoverProjectFolders)
        {
            var candidates = containers
                .SelectMany(c => c.Mounts.Where(m => m.Kind == MountKind.Bind))
                .Select(m => m.Source)
                .Concat(containers.Select(c => c.ComposeConfigFile).Where(f => !string.IsNullOrWhiteSpace(f))!)
                .ToList();

            var discovered = await docker.FindProjectFoldersAsync(candidates, ct).ConfigureAwait(false);

            foreach (var folder in discovered.Where(f =>
                         !options.ExtraPaths.Contains(f, StringComparer.Ordinal) && !options.IsExcluded(f)))
            {
                log.LogInformation("Including project folder {Folder} found next to a container's data", folder);
                options.ExtraPaths.Add(folder);
            }
        }

        // Extra host folders run after the containers are back up. They are normally static project
        // files, so there is no reason to keep services down while they copy.
        for (var index = 0; index < options.ExtraPaths.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var entry = await ArchiveHostPathAsync(
                options.ExtraPaths[index], options, root, run, progress, index, options.ExtraPaths.Count, ct)
                .ConfigureAwait(false);

            if (entry is not null) manifest.ExtraPaths.Add(entry);
        }

        // Networks are captured with their real addressing so a restore onto a fresh host rebuilds
        // them as they were. A container told to trust a proxy at a fixed address does not survive
        // being put on a network Docker numbered differently.
        foreach (var networkName in manifest.Containers
                     .SelectMany(c => c.Networks)
                     .Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();

            var definition = await docker.InspectNetworkAsync(networkName, ct).ConfigureAwait(false);
            if (definition is not null) manifest.Networks.Add(definition);
        }

        foreach (var image in run.SkippedLocalImages.Distinct(StringComparer.Ordinal))
        {
            log.LogWarning(
                "Image {Image} has no registry digest and was NOT archived. Nothing can pull it back; " +
                "consider re-running with image export set to Local only or All.", image);
        }

        await WriteTextAsync(root, BackupManifest.FileName,
            JsonSerializer.Serialize(manifest, ManifestJson), options, ct).ConfigureAwait(false);

        await WriteTextAsync(root, "README.txt",
            BuildReadme(manifest, run.SkippedLocalImages), options, ct).ConfigureAwait(false);

        // A plain-language account of the run, saved beside the data so it is still there months
        // later when the person reading it has forgotten what any of this was.
        await WriteTextAsync(root, ReportFileName,
            ReportBuilders.AfterBackup(manifest, options).ToPlainText(), options, ct).ConfigureAwait(false);

        progress?.Report(new BackupProgress("Finished", root,
            ItemIndex: containers.Count, ItemCount: containers.Count));

        log.LogInformation("Backup complete: {Count} containers, {Size}",
            manifest.Containers.Count, BackupProgress.FormatBytes(manifest.TotalBytes));

        return manifest;
    }

    /// <summary>
    /// Writing a backup onto the same filesystem it came from protects against nothing: the pool
    /// that dies takes both copies. A USB drive is a different mount, and therefore fine.
    /// </summary>
    private async Task WarnIfSameVolumeAsync(string destination, string dockerRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dockerRoot)) return;

        var destinationMount = await remote.GetMountPointAsync(destination, ct).ConfigureAwait(false);
        var sourceMount = await remote.GetMountPointAsync(dockerRoot, ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(destinationMount) &&
            string.Equals(destinationMount, sourceMount, StringComparison.Ordinal))
        {
            log.LogWarning(
                "Backup destination is on {Mount}, the same filesystem as the Docker root. If that " +
                "volume fails you lose the originals and the backup together. Prefer a USB drive or " +
                "this PC.", destinationMount);
        }
    }

    private async Task<ContainerBackup> BackupContainerAsync(
        ContainerInfo container,
        BackupOptions options,
        string root,
        RunContext run,
        IProgress<BackupProgress>? progress,
        int index,
        int total,
        CancellationToken ct)
    {
        var safeName = Sanitise(container.Name);
        var backup = new ContainerBackup
        {
            Name = container.Name,
            Id = container.Id,
            Image = container.Image,
        };

        // 1. The rebuild recipe. Cheap, small, and the part people most regret not having.
        //    Always re-inspected rather than reusing what the Containers tab cached: both the
        //    archived recipe and the recorded run state must reflect the host as it is right now.
        var inspectJson = await docker.InspectAsync(container.Id, ct).ConfigureAwait(false);

        var isRunning = TryReadRunningState(inspectJson) ?? container.IsRunning;
        backup.WasRunning = isRunning;
        backup.Networks = [.. ComposeGenerator.ReadNetworks(inspectJson)];

        var composeMeta = ComposeGenerator.ReadComposeMetadata(inspectJson);
        backup.ComposeProject = composeMeta.Project;
        backup.ComposeService = composeMeta.Service;
        backup.ComposeWorkingDir = composeMeta.WorkingDir;
        backup.ComposeConfigFile = composeMeta.ConfigFile;

        if (composeMeta.IsComposeManaged)
        {
            log.LogInformation("{Container} belongs to compose project {Project} (service {Service}) defined in {File}",
                container.Name, composeMeta.Project, composeMeta.Service, composeMeta.ConfigFile ?? "(unknown)");
        }

        log.LogInformation("{Container} is {State} at backup time, networks: {Networks}",
            container.Name, isRunning ? "running" : "stopped",
            backup.Networks.Count == 0 ? "(default)" : string.Join(", ", backup.Networks));

        var inspectRelative = $"containers/{safeName}.inspect.json";
        await WriteTextAsync(root, inspectRelative, inspectJson, options, ct).ConfigureAwait(false);
        backup.InspectRelativePath = inspectRelative;

        try
        {
            var compose = ComposeGenerator.Generate(inspectJson, container.Name);
            var composeRelative = $"compose/{safeName}.docker-compose.yml";
            await WriteTextAsync(root, composeRelative, compose, options, ct).ConfigureAwait(false);
            backup.ComposeRelativePath = composeRelative;
        }
        catch (Exception ex)
        {
            // Not fatal: the inspect JSON above is the authoritative copy.
            log.LogWarning(ex, "Could not generate compose file for {Container}", container.Name);
        }

        if (!options.IncludeVolumes)
            return backup;

        var mounts = container.Mounts.Where(m => m.IsBackupable).ToList();
        if (mounts.Count == 0)
            log.LogInformation("{Container} has no durable mounts to archive", container.Name);

        // 2. Quiesce before archiving. A tar of a live database is very often a corrupt database.
        var needsStop = options.StopContainersDuringBackup && isRunning && mounts.Count > 0;
        if (needsStop)
        {
            progress?.Report(new BackupProgress("Stopping", container.Name, ItemIndex: index, ItemCount: total));
            await docker.StopContainerAsync(container.Name, ct).ConfigureAwait(false);
            run.StoppedByUs.Add(container.Name);
        }

        foreach (var mount in mounts)
        {
            ct.ThrowIfCancellationRequested();
            var archive = await ArchiveMountAsync(
                container, mount, options, root, safeName, run, progress, index, total, ct)
                .ConfigureAwait(false);

            if (archive is not null) backup.Archives.Add(archive);
        }

        // 3. Images, but only the ones that are actually worth the disk space.
        if (!string.IsNullOrWhiteSpace(container.Image) &&
            await ShouldArchiveImageAsync(container.Image, options, run, ct).ConfigureAwait(false))
        {
            var archive = await ArchiveImageAsync(
                container, options, root, run, progress, index, total, ct).ConfigureAwait(false);

            if (archive is not null) backup.Archives.Add(archive);
        }

        return backup;
    }

    /// <summary>
    /// An image already on a registry can simply be pulled again, so archiving it wastes gigabytes.
    /// One built on the host exists nowhere else, and losing it means losing the container for good.
    /// </summary>
    private async Task<bool> ShouldArchiveImageAsync(
        string image, BackupOptions options, RunContext run, CancellationToken ct)
    {
        if (options.ImageMode == ImageBackupMode.All) return true;

        if (run.Archived.ContainsKey(
                "image:" + await ResolveImageKeyAsync(image, run, ct).ConfigureAwait(false)))
            return true;

        if (!run.ImagePullable.TryGetValue(image, out var pullable))
        {
            pullable = await docker.IsImagePullableAsync(image, ct).ConfigureAwait(false);
            run.ImagePullable[image] = pullable;
        }

        // Unknown means the image is not present locally, so there is nothing to save either way.
        var localOnly = pullable == false;

        if (options.ImageMode == ImageBackupMode.None)
        {
            if (localOnly) run.SkippedLocalImages.Add(image);
            return false;
        }

        return localOnly;
    }

    private async Task<ArchiveEntry?> ArchiveMountAsync(
        ContainerInfo container,
        MountInfo mount,
        BackupOptions options,
        string root,
        string safeContainerName,
        RunContext run,
        IProgress<BackupProgress>? progress,
        int index,
        int total,
        CancellationToken ct)
    {
        var isVolume = mount.Kind == MountKind.Volume;
        var key = isVolume ? "volume:" + mount.Name : "bind:" + mount.Source;

        // Shared between containers: archive once, reference from both.
        if (run.Archived.TryGetValue(key, out var existing))
        {
            log.LogInformation("Reusing archive for {Key} (already captured this run)", key);
            return new ArchiveEntry
            {
                Kind = existing.Kind,
                Source = existing.Source,
                Destination = mount.Destination,
                RelativePath = existing.RelativePath,
                SizeBytes = existing.SizeBytes,
                Sha256 = existing.Sha256,
                IsDirectory = existing.IsDirectory,
            };
        }

        var hostPath = mount.Source;
        if (isVolume)
        {
            // Named volumes live under the docker root, which is not a shared folder and so is
            // invisible to Hyper Backup. Resolve the real path rather than guessing it.
            var resolved = await docker.GetVolumeMountpointAsync(mount.Name, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resolved)) hostPath = resolved;
        }

        if (string.IsNullOrWhiteSpace(hostPath))
        {
            log.LogWarning("Skipping mount with no resolvable host path on {Container}", container.Name);
            return null;
        }

        var label = isVolume ? mount.Name : mount.Destination;
        var fileName = isVolume
            ? $"{safeContainerName}__{Sanitise(mount.Name)}.tar.gz"
            : $"{safeContainerName}__{Sanitise(mount.Destination)}.tar.gz";

        var relative = (isVolume ? "volumes/" : "binds/") + fileName;

        var entry = new ArchiveEntry
        {
            Kind = isVolume ? ArchiveKind.Volume : ArchiveKind.Bind,
            Source = isVolume ? mount.Name : mount.Source,
            Destination = mount.Destination,
            RelativePath = relative,
        };

        var (skipReason, isDirectory) = await ClassifySourceAsync(hostPath, options, ct).ConfigureAwait(false);
        if (skipReason is not null)
        {
            // Not a failure: a container can legitimately reference a path that has been moved
            // away, or mount host plumbing that was never data in the first place.
            log.LogWarning("Not archiving {Path} for {Container}: {Reason}",
                hostPath, container.Name, skipReason);

            entry.SkipReason = skipReason;
            entry.RelativePath = "";
            return entry;
        }

        entry.IsDirectory = isDirectory;

        try
        {
            var estimated = await docker.GetPathSizeBytesAsync(hostPath, ct).ConfigureAwait(false);
            log.LogInformation("Archiving {Kind} {Path} (~{Size} uncompressed) for {Container}",
                isDirectory ? "folder" : "file", hostPath,
                BackupProgress.FormatBytes(estimated), container.Name);

            var result = await CaptureAsync(
                docker.BuildArchiveCommand(hostPath, isDirectory), root, relative, options,
                "Archiving", $"{container.Name} - {label}", estimated, index, total, progress, ct)
                .ConfigureAwait(false);

            // tar exits 1 for "file changed as we read it", which is a warning, not a failure.
            if (result.ExitCode != 0 && result.ExitCode != 1)
                throw new SshCommandException($"tar exited {result.ExitCode}: {result.StdErr}");

            if (result.ExitCode == 1)
                log.LogWarning("tar reported changes during archive of {Path}: {Error}", hostPath, result.StdErr);

            entry.SizeBytes = result.Bytes;
            entry.Sha256 = result.Sha256;
            run.Archived[key] = entry;

            log.LogInformation("Archived {Label} -> {File} ({Size})",
                label, relative, BackupProgress.FormatBytes(result.Bytes));
        }
        catch (OperationCanceledException)
        {
            await TryDeleteAsync(root, relative, options).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // One unreadable mount must not sink the whole run.
            log.LogError(ex, "Failed to archive {Path} for {Container}", hostPath, container.Name);
            entry.Error = ex.Message;
            await TryDeleteAsync(root, relative, options).ConfigureAwait(false);
        }

        return entry;
    }

    /// <summary>
    /// Archives a host folder that no container mounts, such as a project directory holding the
    /// compose file, Dockerfile and .env. Everything runs under sudo, so readability is not a
    /// concern; ownership, mode bits and (where tar supports it) ACLs are preserved.
    /// </summary>
    private async Task<ArchiveEntry?> ArchiveHostPathAsync(
        string hostPath,
        BackupOptions options,
        string root,
        RunContext run,
        IProgress<BackupProgress>? progress,
        int index,
        int total,
        CancellationToken ct)
    {
        var key = "path:" + hostPath;

        // A container may already bind-mount exactly this folder, in which case it is on disk twice
        // for no reason. Reference the existing archive instead.
        if (run.Archived.TryGetValue("bind:" + hostPath, out var asBind))
        {
            log.LogInformation("{Path} was already captured as a bind mount; referencing it", hostPath);
            return new ArchiveEntry
            {
                Kind = ArchiveKind.HostPath,
                Source = hostPath,
                RelativePath = asBind.RelativePath,
                SizeBytes = asBind.SizeBytes,
                Sha256 = asBind.Sha256,
                IsDirectory = asBind.IsDirectory,
            };
        }

        if (run.Archived.TryGetValue(key, out var existing)) return existing;

        var relative = "paths/" + Sanitise(hostPath.Trim('/')) + ".tar.gz";

        var entry = new ArchiveEntry
        {
            Kind = ArchiveKind.HostPath,
            Source = hostPath,
            RelativePath = relative,
        };

        var (skipReason, isDirectory) = await ClassifySourceAsync(hostPath, options, ct).ConfigureAwait(false);
        if (skipReason is not null)
        {
            log.LogWarning("Not archiving host folder {Path}: {Reason}", hostPath, skipReason);
            entry.SkipReason = skipReason;
            entry.RelativePath = "";
            return entry;
        }

        entry.IsDirectory = isDirectory;

        try
        {
            var estimated = await docker.GetPathSizeBytesAsync(hostPath, ct).ConfigureAwait(false);
            log.LogInformation("Archiving host {Kind} {Path} (~{Size})",
                isDirectory ? "folder" : "file", hostPath, BackupProgress.FormatBytes(estimated));

            var result = await CaptureAsync(
                docker.BuildArchiveCommand(hostPath, isDirectory), root, relative, options,
                "Archiving folder", hostPath, estimated, index, total, progress, ct).ConfigureAwait(false);

            if (result.ExitCode != 0 && result.ExitCode != 1)
                throw new SshCommandException($"tar exited {result.ExitCode}: {result.StdErr}");

            if (result.ExitCode == 1)
                log.LogWarning("tar reported changes while archiving {Path}: {Error}", hostPath, result.StdErr);

            entry.SizeBytes = result.Bytes;
            entry.Sha256 = result.Sha256;
            run.Archived[key] = entry;

            log.LogInformation("Archived {Path} -> {File} ({Size})",
                hostPath, relative, BackupProgress.FormatBytes(result.Bytes));
        }
        catch (OperationCanceledException)
        {
            await TryDeleteAsync(root, relative, options).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to archive host folder {Path}", hostPath);
            entry.Error = ex.Message;
            await TryDeleteAsync(root, relative, options).ConfigureAwait(false);
        }

        return entry;
    }

    /// <summary>
    /// The image's content ID when the host can tell us, otherwise the reference as written. The
    /// fallback only loses deduplication, so an image that cannot be inspected is still archived.
    /// </summary>
    private async Task<string> ResolveImageKeyAsync(string image, RunContext run, CancellationToken ct)
    {
        if (!run.ImageIds.TryGetValue(image, out var id))
        {
            id = await docker.GetImageIdAsync(image, ct).ConfigureAwait(false);
            run.ImageIds[image] = id;
        }

        return string.IsNullOrWhiteSpace(id) ? image : id;
    }

    private async Task<ArchiveEntry?> ArchiveImageAsync(
        ContainerInfo container,
        BackupOptions options,
        string root,
        RunContext run,
        IProgress<BackupProgress>? progress,
        int index,
        int total,
        CancellationToken ct)
    {
        // Key on the image's content ID, not on how this container happens to spell it. Two
        // containers naming one image as "postgres" and "postgres:latest" would otherwise each get
        // their own copy, and a full image export is where the gigabytes are.
        var key = "image:" + await ResolveImageKeyAsync(container.Image, run, ct).ConfigureAwait(false);
        if (run.Archived.TryGetValue(key, out var existing)) return existing;

        var relative = "images/" + Sanitise(container.Image) + ".tar.gz";

        var entry = new ArchiveEntry
        {
            Kind = ArchiveKind.Image,
            Source = container.Image,
            RelativePath = relative,
        };

        try
        {
            var pullable = run.ImagePullable.GetValueOrDefault(container.Image);
            log.LogInformation("Saving image {Image} ({Reason})", container.Image,
                pullable == false ? "local only, cannot be pulled again" : "image export set to All");

            var result = await CaptureAsync(
                docker.BuildImageSaveCommand(container.Image), root, relative, options,
                "Saving image", container.Image, 0, index, total, progress, ct).ConfigureAwait(false);

            if (result.ExitCode != 0)
                throw new SshCommandException($"docker save exited {result.ExitCode}: {result.StdErr}");

            entry.SizeBytes = result.Bytes;
            entry.Sha256 = result.Sha256;
            run.Archived[key] = entry;
        }
        catch (OperationCanceledException)
        {
            await TryDeleteAsync(root, relative, options).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to save image {Image}", container.Image);
            entry.Error = ex.Message;
            await TryDeleteAsync(root, relative, options).ConfigureAwait(false);
        }

        return entry;
    }

    /// <summary>
    /// Runs a command that writes an archive to stdout and puts the bytes wherever the backup lives.
    ///
    /// Locally that means streaming stdout back over SSH into a file here. On the host it means
    /// redirecting into a file there, which never crosses the network; since there is no byte stream
    /// to watch in that case, progress comes from measuring the growing file instead.
    /// </summary>
    private async Task<CaptureResult> CaptureAsync(
        string producerCommand,
        string root,
        string relative,
        BackupOptions options,
        string stage,
        string item,
        long estimatedBytes,
        int index,
        int total,
        IProgress<BackupProgress>? progress,
        CancellationToken ct)
    {
        if (!options.DestinationOnHost)
        {
            var fullPath = EnsureLocalPath(root, relative);

            var reporter = new Progress<long>(bytes => progress?.Report(new BackupProgress(
                stage, item, bytes, estimatedBytes > 0 ? estimatedBytes : null, index, total)));

            await using var file = File.Create(fullPath);
            var streamed = await ssh.DownloadAsync(
                producerCommand, file, Elevate, options.ComputeChecksums, reporter, ct).ConfigureAwait(false);

            return new CaptureResult(streamed.Bytes, streamed.Sha256, streamed.ExitCode, streamed.StdErr);
        }

        var remotePath = IRemoteFileSystem.Combine(root, relative);
        var parent = ParentOf(remotePath);
        if (parent.Length > 0) await remote.CreateDirectoryAsync(parent, ct).ConfigureAwait(false);

        var command = $"{producerCommand} > {SshSession.Quote(remotePath)}";
        var runTask = ssh.RunAsync(command, Elevate, ct);

        // No stream to count, so watch the file grow instead. The estimate is of uncompressed size,
        // so it is not comparable with the compressed file: report bytes written, not a percentage.
        while (true)
        {
            var finished = await Task.WhenAny(runTask, Task.Delay(RemotePollInterval, ct)).ConfigureAwait(false);
            if (finished == runTask) break;

            var written = await remote.GetFileSizeAsync(remotePath, ct).ConfigureAwait(false);
            progress?.Report(new BackupProgress(stage, item, written, null, index, total));
        }

        var result = await runTask.ConfigureAwait(false);

        var size = await remote.GetFileSizeAsync(remotePath, ct).ConfigureAwait(false);
        var sha = options.ComputeChecksums
            ? await remote.ComputeSha256Async(remotePath, ct).ConfigureAwait(false)
            : null;

        return new CaptureResult(size, sha, result.ExitCode, result.StdErr);
    }

    private async Task WriteTextAsync(
        string root, string relative, string content, BackupOptions options, CancellationToken ct)
    {
        if (!options.DestinationOnHost)
        {
            await File.WriteAllTextAsync(EnsureLocalPath(root, relative), content, ct).ConfigureAwait(false);
            return;
        }

        var remotePath = IRemoteFileSystem.Combine(root, relative);
        var parent = ParentOf(remotePath);
        if (parent.Length > 0) await remote.CreateDirectoryAsync(parent, ct).ConfigureAwait(false);

        await remote.WriteTextAsync(remotePath, content, ct).ConfigureAwait(false);
    }

    private async Task TryDeleteAsync(string root, string relative, BackupOptions options)
    {
        try
        {
            if (options.DestinationOnHost)
            {
                var remotePath = IRemoteFileSystem.Combine(root, relative);
                await ssh.RunAsync($"rm -f {SshSession.Quote(remotePath)}", Elevate, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            var fullPath = Path.Combine(root, relative);
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch (Exception ex)
        {
            // A leftover partial file is not worth failing the run over.
            log.LogDebug(ex, "Could not remove partial archive {Relative}", relative);
        }
    }

    private static string ParentOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "" : path[..index];
    }

    private static string EnsureLocalPath(string root, string relative)
    {
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    /// <summary>
    /// Reads State.Running out of `docker inspect`. Returns null when the payload is not shaped as
    /// expected, so the caller can fall back rather than guessing "stopped" and losing the state.
    /// </summary>
    private static bool? TryReadRunningState(string inspectJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(inspectJson);

            var container = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().FirstOrDefault()
                : doc.RootElement;

            if (container.ValueKind != JsonValueKind.Object) return null;
            if (!container.TryGetProperty("State", out var state)) return null;
            if (!state.TryGetProperty("Running", out var running)) return null;

            return running.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildReadme(BackupManifest manifest, List<string> skippedLocalImages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SshDockerBackup backup set");
        sb.AppendLine("==========================");
        sb.AppendLine();
        sb.AppendLine($"Created:  {manifest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Host:     {manifest.HostDescription}");
        sb.AppendLine($"Docker:   {manifest.DockerVersion} (root dir {manifest.DockerRootDir})");
        sb.AppendLine($"Stored:   {(manifest.StoredOnHost ? "on the host itself" : "on a PC over SSH")}");
        sb.AppendLine($"Contents: {manifest.Containers.Count} containers, {BackupProgress.FormatBytes(manifest.TotalBytes)}");
        sb.AppendLine();
        sb.AppendLine("Layout");
        sb.AppendLine("------");
        sb.AppendLine("  manifest.json   Index of everything here. Restore reads this first.");
        sb.AppendLine("  containers/     Raw `docker inspect` per container: the authoritative rebuild recipe.");
        sb.AppendLine("  compose/        Generated docker-compose files. Best effort, review before use.");
        sb.AppendLine("  volumes/        Named-volume data as gzipped tar (--numeric-owner).");
        sb.AppendLine("  binds/          Bind-mount data as gzipped tar.");
        sb.AppendLine("  paths/          Host folders captured by request: project directories and the like.");
        sb.AppendLine("  images/         Images that could not be pulled again, plus any others you asked for.");
        sb.AppendLine();

        if (manifest.ExtraPaths.Count > 0)
        {
            sb.AppendLine("Host folders captured");
            sb.AppendLine("---------------------");
            foreach (var extra in manifest.ExtraPaths)
            {
                sb.AppendLine(extra.Succeeded
                    ? $"  {extra.Source}  ({BackupProgress.FormatBytes(extra.SizeBytes)})"
                    : $"  {extra.Source}  FAILED: {extra.Error}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Run state");
        sb.AppendLine("---------");
        var running = manifest.Containers.Count(c => c.WasRunning);
        sb.AppendLine($"  {running} running and {manifest.Containers.Count - running} stopped when this was taken.");
        sb.AppendLine("  Restore reproduces that by default: a container that was stopped is created but");
        sb.AppendLine("  not started.");
        sb.AppendLine();
        sb.AppendLine("SECURITY");
        sb.AppendLine("--------");
        sb.AppendLine("The inspect JSON and the compose files contain every container environment");
        sb.AppendLine("variable in plaintext, which for most stacks means database passwords, API keys");
        sb.AppendLine("and tokens. Treat this folder as sensitive and encrypt it before moving it");
        sb.AppendLine("off-site or into cloud storage.");
        sb.AppendLine();

        var distinctSkipped = skippedLocalImages.Distinct(StringComparer.Ordinal).ToList();
        if (distinctSkipped.Count > 0)
        {
            sb.AppendLine("IMAGES NOT CAPTURED");
            sb.AppendLine("-------------------");
            sb.AppendLine("These images have no registry digest, so no registry can supply them again,");
            sb.AppendLine("and image export was switched off for this run:");
            foreach (var image in distinctSkipped)
                sb.AppendLine($"  {image}");
            sb.AppendLine();
        }

        var skipped = manifest.SkippedArchives.ToList();
        if (skipped.Count > 0)
        {
            sb.AppendLine("NOT ARCHIVED (deliberately)");
            sb.AppendLine("---------------------------");
            sb.AppendLine("These were not captured, and that is expected rather than a failure. A");
            sb.AppendLine("container can reference a folder that has since been moved away, or mount");
            sb.AppendLine("host plumbing such as docker.sock that was never data to begin with.");
            sb.AppendLine();
            foreach (var item in skipped)
                sb.AppendLine($"  {item.Kind} {item.Source}: {item.SkipReason}");
            sb.AppendLine();
        }

        var failed = manifest.FailedArchives.ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine("FAILED");
            sb.AppendLine("------");
            sb.AppendLine("These were meant to be captured but could not be. Worth investigating.");
            sb.AppendLine();
            foreach (var item in failed)
                sb.AppendLine($"  {item.Kind} {item.Source}: {item.Error}");
        }

        return sb.ToString();
    }

    /// <summary>Makes a string safe as a filename without losing readability.</summary>
    private static string Sanitise(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unnamed";

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c =>
            invalid.Contains(c) || c is '/' or '\\' or ':' ? '_' : c).ToArray())
            .Trim('_', ' ', '.');

        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }
}
