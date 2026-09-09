using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SshDockerBackup.Core.Docker;
using SshDockerBackup.Core.Models;
using SshDockerBackup.Core.Remote;
using SshDockerBackup.Core.Ssh;
using SshDockerBackup.Core.Synology;

namespace SshDockerBackup.Core.Backup;

/// <summary>How a restore line should read: something done, something worth knowing, or a problem.</summary>
public enum RestoreNoteKind
{
    Done,
    Note,
    Problem,
}

/// <summary>
/// One line of what a restore did. The kind is set where the line is written rather than guessed
/// from its wording later: a note that reads "not added, because the folder is gone" is neither a
/// success nor a failure, and no amount of substring matching reliably tells the three apart.
/// </summary>
public sealed record RestoreNote(string Text, RestoreNoteKind Kind = RestoreNoteKind.Done)
{
    /// <summary>Keeps the many ordinary lines as plain strings at the point they are written.</summary>
    public static implicit operator RestoreNote(string text) => new(text);

    public override string ToString() => Text;
}

public sealed record RestoreOutcome(int Restored, int Failed, IReadOnlyList<RestoreNote> Messages);

/// <summary>
/// Whether a restore can actually complete, checked before anything is written. The expensive
/// surprise is an image that is neither on the host nor in the backup, on a NAS with no internet.
/// </summary>
public sealed record RestorePreflight(
    IReadOnlyList<string> ImagesAlreadyOnHost,
    IReadOnlyList<string> ImagesFromBackup,
    IReadOnlyList<string> ImagesNeedingDownload,
    bool? RegistryReachable)
{
    /// <summary>Something must be downloaded and the host says it cannot reach a registry.</summary>
    public bool WillFailOffline => ImagesNeedingDownload.Count > 0 && RegistryReachable == false;

    /// <summary>Something must be downloaded but reachability could not be established.</summary>
    public bool DownloadUnverified => ImagesNeedingDownload.Count > 0 && RegistryReachable is null;
}

public interface IRestoreService
{
    /// <summary>Checks image availability and internet access without writing anything.</summary>
    Task<RestorePreflight> PreflightAsync(
        IReadOnlyList<ContainerBackup> containers,
        RestoreOptions options,
        CancellationToken ct = default);

    /// <param name="onHost">True when the backup folder lives on the Docker host, not on this PC.</param>
    Task<BackupManifest> LoadManifestAsync(string backupRoot, bool onHost, CancellationToken ct = default);

    Task<RestoreOutcome> RestoreAsync(
        BackupManifest manifest,
        IReadOnlyList<ContainerBackup> containers,
        IReadOnlyList<ArchiveEntry> extraPaths,
        RestoreOptions options,
        IProgress<BackupProgress>? progress,
        CancellationToken ct = default);
}

/// <summary>
/// Pushes a backup set back onto a Docker host: host folders first, then volume data, then the
/// containers themselves.
///
/// A set stored on the host restores without touching the network at all: tar reads the archive
/// straight off the USB drive. A set stored on this PC is streamed back over SSH.
/// </summary>
public sealed class RestoreService(
    ISshSession ssh,
    IDockerService docker,
    IRemoteFileSystem remote,
    ISynologyProjects synology,
    ILogger<RestoreService> log) : IRestoreService
{
    private const string RemoteStagingDir = "/tmp/sshdockerbackup";
    private string? _composeCommand;

    private bool Elevate => ssh.Settings?.UseSudo ?? true;

    public async Task<BackupManifest> LoadManifestAsync(
        string backupRoot, bool onHost, CancellationToken ct = default)
    {
        string json;

        if (onHost)
        {
            var path = IRemoteFileSystem.Combine(backupRoot, BackupManifest.FileName);
            if (!await remote.FileExistsAsync(path, ct).ConfigureAwait(false))
            {
                throw new FileNotFoundException(
                    $"No {BackupManifest.FileName} in {backupRoot} on the host. Is this a backup folder?", path);
            }

            json = await remote.ReadTextAsync(path, ct).ConfigureAwait(false);
        }
        else
        {
            var path = Path.Combine(backupRoot, BackupManifest.FileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"No {BackupManifest.FileName} in {backupRoot}. Is this a backup folder?", path);
            }

            json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(json)
                       ?? throw new InvalidOperationException("The manifest could not be read.");

        manifest.RootPath = backupRoot;
        manifest.StoredOnHost = onHost;

        log.LogInformation("Loaded manifest: {Count} containers from {Host} ({Where})",
            manifest.Containers.Count, manifest.HostDescription, onHost ? "on the host" : "on this PC");

        return manifest;
    }

    /// <summary>
    /// "postgres" and "postgres:latest" name the same image, so an untagged reference gets the tag
    /// Docker would apply. A colon in the registry part ("myreg:5000/app") is a port, not a tag,
    /// which is why only the last path segment is examined.
    /// </summary>
    private static string NormaliseImageRef(string image)
    {
        var reference = image.Trim();
        if (reference.Length == 0) return reference;

        var lastSegment = reference[(reference.LastIndexOf('/') + 1)..];
        return lastSegment.Contains(':') || lastSegment.Contains('@')
            ? reference
            : reference + ":latest";
    }

    public async Task<RestorePreflight> PreflightAsync(
        IReadOnlyList<ContainerBackup> containers,
        RestoreOptions options,
        CancellationToken ct = default)
    {
        var wanted = containers
            .Select(c => c.Image)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var missing = await docker.FindMissingImagesAsync(wanted, ct).ConfigureAwait(false);
        var onHost = wanted.Except(missing, StringComparer.Ordinal).ToList();

        // An image missing from the host is only a problem if the backup cannot supply it either.
        // Compared on the normalised reference: one image saved once can be named "postgres" by one
        // container and "postgres:latest" by another, and reporting it as a download would be wrong.
        var archived = options.LoadImages
            ? containers
                .SelectMany(c => c.Archives)
                .Where(a => a.Kind == ArchiveKind.Image && a.Succeeded)
                .Select(a => NormaliseImageRef(a.Source))
                .ToHashSet(StringComparer.Ordinal)
            : [];

        var fromBackup = missing.Where(i => archived.Contains(NormaliseImageRef(i))).ToList();
        var mustDownload = missing.Where(i => !archived.Contains(NormaliseImageRef(i))).ToList();

        // Only worth asking the host about the internet when something actually has to come over it.
        var reachable = mustDownload.Count > 0
            ? await docker.CanReachRegistryAsync(ct).ConfigureAwait(false)
            : (bool?)null;

        log.LogInformation(
            "Restore pre-flight: {OnHost} image(s) already present, {FromBackup} from the backup, " +
            "{Download} need downloading, registry reachable: {Reachable}",
            onHost.Count, fromBackup.Count, mustDownload.Count, reachable?.ToString() ?? "not checked");

        return new RestorePreflight(onHost, fromBackup, mustDownload, reachable);
    }

    public async Task<RestoreOutcome> RestoreAsync(
        BackupManifest manifest,
        IReadOnlyList<ContainerBackup> containers,
        IReadOnlyList<ArchiveEntry> extraPaths,
        RestoreOptions options,
        IProgress<BackupProgress>? progress,
        CancellationToken ct = default)
    {
        if (containers.Count == 0 && extraPaths.Count == 0)
            throw new InvalidOperationException("Nothing was selected.");

        progress?.Report(new BackupProgress("Preparing", "Checking host"));
        await docker.ProbeAsync(ct).ConfigureAwait(false);

        var messages = new List<RestoreNote>();
        var restored = 0;
        var failed = 0;

        // Host folders go back first: a container may need a config file or script from its project
        // directory the moment it starts.
        if (options.RestoreExtraPaths)
        {
            for (var index = 0; index < extraPaths.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = extraPaths[index];

                try
                {
                    await RestoreArchiveAsync(options.BackupRoot, entry.Source, entry, options,
                        progress, index, extraPaths.Count, ct).ConfigureAwait(false);

                    restored++;
                    messages.Add($"{entry.Source}: folder restored.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.LogError(ex, "Restore failed for host folder {Path}", entry.Source);
                    messages.Add($"{entry.Source}: {ex.Message}");
                }
            }
        }

        // Two phases, deliberately. A container's compose file can live inside another container's
        // bind mount — a Portainer stack is exactly that — so every byte has to be on disk before
        // anything is recreated. Interleaving the two only works if the containers happen to be in
        // a lucky order, which is no basis for a disaster recovery onto a fresh machine.
        var broken = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < containers.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var container = containers[index];

            progress?.Report(new BackupProgress("Restoring data", container.Name,
                ItemIndex: index, ItemCount: containers.Count));

            try
            {
                await RestoreContainerDataAsync(container, options, progress, index,
                    containers.Count, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                broken.Add(container.Name);
                log.LogError(ex, "Restoring data failed for {Container}", container.Name);
                messages.Add($"{container.Name}: {ex.Message}");
            }
        }

        if (options.RecreateContainers)
        {
            for (var index = 0; index < containers.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var container = containers[index];

                // Recreating a container whose own data failed to land would start it against
                // whatever is on disk, which is worse than leaving it alone.
                if (broken.Contains(container.Name)) continue;

                progress?.Report(new BackupProgress("Recreating", container.Name,
                    ItemIndex: index, ItemCount: containers.Count));

                try
                {
                    await RecreateContainerAsync(options.BackupRoot, container, manifest, options, messages, ct)
                        .ConfigureAwait(false);
                    restored++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.LogError(ex, "Recreating failed for {Container}", container.Name);
                    messages.Add($"{container.Name}: {ex.Message}");
                }
            }
        }
        else
        {
            restored += containers.Count - broken.Count;
        }

        if (options.RecreateContainers && options.RegisterSynologyProjects)
        {
            progress?.Report(new BackupProgress("Registering projects", "Container Manager"));
            failed += await RegisterProjectsAsync(containers, messages, ct).ConfigureAwait(false);
        }

        progress?.Report(new BackupProgress("Finished", $"{restored} restored, {failed} failed",
            ItemIndex: containers.Count, ItemCount: containers.Count));

        return new RestoreOutcome(restored, failed, messages);
    }

    /// <summary>Phase one: everything that lands on disk, before any container is recreated.</summary>
    private async Task RestoreContainerDataAsync(
        ContainerBackup container,
        RestoreOptions options,
        IProgress<BackupProgress>? progress,
        int index,
        int total,
        CancellationToken ct)
    {
        var root = options.BackupRoot;

        if (options.LoadImages)
        {
            foreach (var image in container.Archives.Where(a => a.Kind == ArchiveKind.Image && a.Succeeded))
                await LoadImageAsync(root, image, options, progress, index, total, ct).ConfigureAwait(false);
        }

        if (!options.RestoreVolumeData) return;

        var dataArchives = container.Archives
            .Where(a => a.Kind is ArchiveKind.Volume or ArchiveKind.Bind && a.Succeeded)
            .ToList();

        foreach (var archive in dataArchives)
        {
            ct.ThrowIfCancellationRequested();
            await RestoreArchiveAsync(root, container.Name, archive, options, progress, index, total, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task RestoreArchiveAsync(
        string root,
        string label,
        ArchiveEntry archive,
        RestoreOptions options,
        IProgress<BackupProgress>? progress,
        int index,
        int total,
        CancellationToken ct)
    {
        // Locate the archive, wherever the set happens to live.
        string localPath = "";
        string remoteArchivePath = "";
        long archiveSize;

        if (options.SourceOnHost)
        {
            remoteArchivePath = IRemoteFileSystem.Combine(root, archive.RelativePath);
            if (!await remote.FileExistsAsync(remoteArchivePath, ct).ConfigureAwait(false))
                throw new FileNotFoundException($"Archive missing from the backup set: {archive.RelativePath}");

            archiveSize = await remote.GetFileSizeAsync(remoteArchivePath, ct).ConfigureAwait(false);
        }
        else
        {
            localPath = Path.Combine(root, archive.RelativePath);
            if (!File.Exists(localPath))
                throw new FileNotFoundException($"Archive missing from the backup set: {archive.RelativePath}", localPath);

            archiveSize = new FileInfo(localPath).Length;
        }

        if (options.VerifyChecksums && archive.Sha256 is { Length: > 0 })
        {
            progress?.Report(new BackupProgress("Verifying", archive.RelativePath, ItemIndex: index, ItemCount: total));

            var actual = options.SourceOnHost
                ? await remote.ComputeSha256Async(remoteArchivePath, ct).ConfigureAwait(false)
                : await ComputeSha256Async(localPath, ct).ConfigureAwait(false);

            if (actual is null)
            {
                log.LogWarning("Could not compute a checksum for {Archive}; continuing unverified",
                    archive.RelativePath);
            }
            else if (!string.Equals(actual, archive.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Checksum mismatch for {archive.RelativePath}. The archive is damaged; " +
                    "restoring it would write corrupt data.");
            }
        }

        // Work out where this needs to land on the host.
        string targetPath;
        if (archive.Kind == ArchiveKind.Volume)
        {
            var volumeName = archive.Source;
            if (!await docker.VolumeExistsAsync(volumeName, ct).ConfigureAwait(false))
            {
                log.LogInformation("Creating missing volume {Volume}", volumeName);
                await docker.CreateVolumeAsync(volumeName, ct).ConfigureAwait(false);
            }

            targetPath = await docker.GetVolumeMountpointAsync(volumeName, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException($"Could not resolve mountpoint for volume {volumeName}.");
        }
        else if (archive.IsDirectory)
        {
            // Bind mounts and requested host folders both restore straight to their original path.
            targetPath = archive.Source;
            await docker.EnsureDirectoryAsync(targetPath, ct).ConfigureAwait(false);
        }
        else
        {
            // A single-file bind mount was archived by name from its parent, so it unpacks there.
            // Creating a directory at the file's own path would be actively wrong.
            targetPath = ParentOf(archive.Source);
            await docker.EnsureDirectoryAsync(targetPath, ct).ConfigureAwait(false);
        }

        log.LogInformation("Extracting {Archive} into {Target}", archive.RelativePath, targetPath);

        if (options.SourceOnHost)
        {
            // The archive is already beside the host, so tar reads it directly. Nothing crosses
            // the network, and there is no byte stream to report progress from.
            progress?.Report(new BackupProgress("Restoring data", $"{label} - {archive.Source}",
                0, null, index, total));

            var extract = await ssh.RunAsync(
                docker.BuildExtractFromFileCommand(remoteArchivePath, targetPath), Elevate, ct)
                .ConfigureAwait(false);

            if (!extract.Success)
                throw new SshCommandException($"tar extract exited {extract.ExitCode}: {extract.StdErr}");

            return;
        }

        var reporter = new Progress<long>(bytes => progress?.Report(new BackupProgress(
            "Restoring data", $"{label} - {archive.Source}", bytes, archiveSize, index, total)));

        await using var file = File.OpenRead(localPath);
        var result = await ssh.UploadAsync(
            docker.BuildExtractCommand(targetPath), file, Elevate, reporter, ct).ConfigureAwait(false);

        if (!result.Success)
            throw new SshCommandException($"tar extract exited {result.ExitCode}: {result.StdErr}");
    }

    private async Task LoadImageAsync(
        string root, ArchiveEntry archive, RestoreOptions options, IProgress<BackupProgress>? progress,
        int index, int total, CancellationToken ct)
    {
        if (options.SourceOnHost)
        {
            var remoteArchive = IRemoteFileSystem.Combine(root, archive.RelativePath);
            if (!await remote.FileExistsAsync(remoteArchive, ct).ConfigureAwait(false))
            {
                log.LogWarning("Image archive missing, will rely on a registry pull: {Path}", archive.RelativePath);
                return;
            }

            progress?.Report(new BackupProgress("Loading image", archive.Source, 0, null, index, total));

            var loaded = await ssh.RunAsync(
                docker.BuildImageLoadFromFileCommand(remoteArchive), Elevate, ct).ConfigureAwait(false);

            if (!loaded.Success)
                throw new SshCommandException($"docker load exited {loaded.ExitCode}: {loaded.StdErr}");

            log.LogInformation("Loaded image {Image}", archive.Source);
            return;
        }

        var localPath = Path.Combine(root, archive.RelativePath);
        if (!File.Exists(localPath))
        {
            log.LogWarning("Image archive missing, will rely on a registry pull: {Path}", archive.RelativePath);
            return;
        }

        var size = new FileInfo(localPath).Length;
        var reporter = new Progress<long>(bytes => progress?.Report(new BackupProgress(
            "Loading image", archive.Source, bytes, size, index, total)));

        await using var file = File.OpenRead(localPath);
        var result = await ssh.UploadAsync(docker.BuildImageLoadCommand(), file, Elevate, reporter, ct)
            .ConfigureAwait(false);

        if (!result.Success)
            throw new SshCommandException($"docker load exited {result.ExitCode}: {result.StdErr}");

        log.LogInformation("Loaded image {Image}", archive.Source);
    }

    private async Task RecreateContainerAsync(
        string root, ContainerBackup container, BackupManifest manifest, RestoreOptions options, List<RestoreNote> messages, CancellationToken ct)
    {
        var exists = await ContainerExistsAsync(container.Name, ct).ConfigureAwait(false);
        if (exists)
        {
            if (!options.RemoveExistingContainers)
            {
                messages.Add($"{container.Name}: a container with this name already exists; skipped. " +
                             "Enable \"Replace existing containers\" to remove and recreate it.");
                return;
            }

            log.LogWarning("Removing existing container {Container} before recreating it", container.Name);
            await docker.RunDockerAsync($"rm -f {SshSession.Quote(container.Name)}", ct).ConfigureAwait(false);
        }

        var (remoteFile, projectDirectory, service, usedOriginal) =
            await ResolveComposeFileAsync(root, container, manifest, options, messages, ct).ConfigureAwait(false);

        if (remoteFile is null) return;

        // Only the generated file needs this. It declares its networks "external: true", so they
        // must already exist. The project's own file usually declares compose-managed networks, and
        // compose refuses to adopt a network it did not create — it checks for its own label and
        // fails with "has incorrect label com.docker.compose.network". Pre-creating one there
        // actively breaks the restore.
        if (!usedOriginal)
            await EnsureNetworksAsync(container, manifest, messages, ct).ConfigureAwait(false);

        var compose = await ResolveComposeCommandAsync(ct).ConfigureAwait(false);

        // The original project name matters beyond tidiness: Synology's Project view groups by the
        // com.docker.compose.project label, so recreating under the container's own name leaves the
        // container running but listed under no project at all.
        var project = container.ComposeProject is { Length: > 0 } original
            ? original
            : SanitiseRemote(container.Name);

        var shouldStart = !options.PreserveRunningState || container.WasRunning;

        // "create" rather than "up -d then stop": a container that was deliberately left stopped
        // must not get a moment of runtime, which for a database can mean running migrations.
        var verb = shouldStart ? "up -d" : "create";

        var builder = new StringBuilder(compose);
        builder.Append(" -p ").Append(SshSession.Quote(project));

        if (projectDirectory is { Length: > 0 })
            builder.Append(" --project-directory ").Append(SshSession.Quote(projectDirectory));

        builder.Append(" -f ").Append(SshSession.Quote(remoteFile)).Append(' ').Append(verb);

        // Target the single service, so recreating one container does not bring up its whole project.
        if (usedOriginal && service is { Length: > 0 })
            builder.Append(' ').Append(SshSession.Quote(service));

        var command = builder.ToString();

        log.LogInformation("Recreating {Container} ({State} at backup time, project {Project}, {Source}) with: {Command}",
            container.Name, container.WasRunning ? "running" : "stopped", project,
            usedOriginal ? "original compose file" : "generated compose file", command);

        var result = await ssh.RunAsync(command, Elevate, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            var hint = result.StdErr.Contains("was not created by compose", StringComparison.OrdinalIgnoreCase) ||
                       result.StdErr.Contains("incorrect label com.docker.compose.network", StringComparison.OrdinalIgnoreCase)
                ? $"{Environment.NewLine}A network exists that compose did not create, so it will not adopt it. " +
                  $"Remove it and run this again: docker network rm <name>"
                : "";

            throw new SshCommandException(
                $"compose {verb} failed for {container.Name}: {result.StdErr}{hint}{Environment.NewLine}" +
                $"The compose file is on the host at {remoteFile} if you want to run it by hand.");
        }

        messages.Add(shouldStart
            ? $"{container.Name}: recreated and started."
            : $"{container.Name}: recreated, left stopped (it was not running when the backup was taken).");

        // The project's Container Manager entry is handled once for the whole run, after every
        // container is back — see RegisterProjectsAsync.
    }

    /// <summary>
    /// Puts back the Container Manager project entries, which are DSM's own bookkeeping rather than
    /// anything Docker knows about. Done once per run, after every container exists, so DSM adopts
    /// them all in one go.
    /// </summary>
    /// <summary>
    /// Builds a freshly registered project, retrying a refusal. Projects are registered one after
    /// another and each build starts work of its own inside DSM, so a build issued while the last
    /// one is still settling gets turned away with error 2104 -- observed on the third of three,
    /// which a manual Build moments later then completed. Repeating is safe: a build amounts to
    /// "compose up" on containers that are already running.
    /// </summary>
    private async Task<bool> BuildWithRetryAsync(string id, CancellationToken ct)
    {
        const int attempts = 3;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (await synology.BuildAsync(id, ct).ConfigureAwait(false)) return true;
            if (attempt == attempts) break;

            log.LogInformation(
                "DSM would not build project {Id}; waiting and trying again (attempt {Next} of {Total})",
                id, attempt + 1, attempts);

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <returns>How many projects could not be registered, so the run can report them as failures.</returns>
    private async Task<int> RegisterProjectsAsync(
        IReadOnlyList<ContainerBackup> containers, List<RestoreNote> messages, CancellationToken ct)
    {
        var failures = 0;

        var projects = containers
            .Where(c => c.ComposeProject is { Length: > 0 } && c.ComposeWorkingDir is { Length: > 0 })
            .GroupBy(c => c.ComposeProject!, StringComparer.Ordinal)
            .Select(group => (Name: group.Key, Path: group.First().ComposeWorkingDir!))
            .ToList();

        if (projects.Count == 0) return failures;

        try
        {
            if (!await synology.IsAvailableAsync(ct).ConfigureAwait(false)) return failures;

            var existing = await synology.ListAsync(ct).ConfigureAwait(false);

            foreach (var (name, path) in projects)
            {
                if (existing.ContainsKey(name))
                {
                    log.LogDebug("Container Manager already lists project {Name}", name);
                    continue;
                }

                // Container Manager adopts a project by pointing at a folder on the host, so a
                // recorded working directory that is not there cannot be registered by anyone. Two
                // ordinary reasons: the folder has since been deleted, or the stack was run by
                // something else that keeps its compose file inside its own volume, which makes the
                // recorded path a container path rather than a host one. Neither is a restore
                // failure, and neither is worth sending someone hunting for a folder to point at.
                if (await docker.InspectPathAsync(path, ct).ConfigureAwait(false) != RemotePathKind.Directory)
                {
                    log.LogInformation(
                        "Not registering project {Name}: {Path} does not exist on the host", name, path);

                    messages.Add(new RestoreNote(
                        $"{name}: not added to Container Manager, because {path} does not exist on " +
                        "this host. Either the project folder is gone, or the stack was managed by " +
                        "something other than Container Manager (Portainer, for instance, keeps its " +
                        "compose files inside its own volume) and that path was never on the host. " +
                        "The containers themselves are restored and running either way.",
                        RestoreNoteKind.Note));
                    continue;
                }

                if (!await synology.CreateAsync(name, path, ct).ConfigureAwait(false))
                {
                    failures++;
                    messages.Add(new RestoreNote(
                        $"{name}: could not be re-registered with Container Manager. Add it by hand " +
                        $"with Project > Create pointing at {path}; the compose file is already there.",
                        RestoreNoteKind.Problem));
                    continue;
                }

                // A new entry lands in CREATED, which leaves the Project row grey with its Start
                // action disabled. Build is the transition DSM itself offers, and it needs the id
                // that only exists once the project has been created.
                var refreshed = await synology.ListAsync(ct).ConfigureAwait(false);

                if (refreshed.TryGetValue(name, out var project) &&
                    await BuildWithRetryAsync(project.Id, ct).ConfigureAwait(false))
                {
                    messages.Add($"{name}: re-registered as a Container Manager project and started.");
                }
                else
                {
                    messages.Add(new RestoreNote(
                        $"{name}: re-registered as a Container Manager project, but it may show as " +
                        "stopped until you run Action > Build on it once. Its containers are running.",
                        RestoreNoteKind.Note));
                }
            }
        }
        catch (Exception ex)
        {
            // DSM bookkeeping must never fail a restore that otherwise worked.
            log.LogWarning(ex, "Could not register Container Manager projects");
            messages.Add(new RestoreNote(
                "Container Manager project registration was skipped; the containers themselves are fine.",
                RestoreNoteKind.Note));
        }

        return failures;
    }

    /// <summary>
    /// Picks which compose file to run. The project's own file is preferred whenever it is still on
    /// the host: it is the real definition rather than a reconstruction from inspect, and running it
    /// keeps the container inside the project Container Manager knows about.
    /// </summary>
    private async Task<(string? File, string? ProjectDirectory, string? Service, bool UsedOriginal)>
        ResolveComposeFileAsync(
            string root, ContainerBackup container, BackupManifest manifest, RestoreOptions options,
            List<RestoreNote> messages, CancellationToken ct)
    {
        if (container.ComposeConfigFile is { Length: > 0 } original &&
            container.ComposeService is { Length: > 0 })
        {
            if (await remote.FileExistsAsync(original, ct).ConfigureAwait(false))
            {
                log.LogInformation("Using the project's own compose file at {File}", original);
                return (original, container.ComposeWorkingDir, container.ComposeService, true);
            }

            // A stack deployed by Portainer records the path its own container saw, such as
            // /data/compose/1/docker-compose.yml. That is a real file on the host, just behind a
            // bind mount — and the backup knows every bind mount, so the path can be translated.
            var translated = TranslateThroughBindMounts(original, manifest);

            if (translated is not null &&
                await remote.FileExistsAsync(translated, ct).ConfigureAwait(false))
            {
                var directory = ParentOf(translated);
                log.LogInformation(
                    "Compose file {Original} is a container path; resolved through a bind mount to {Translated}",
                    original, translated);

                return (translated, directory, container.ComposeService, true);
            }

            log.LogInformation(
                "Project compose file {File} is not on the host; falling back to the generated one", original);
        }

        if (container.ComposeRelativePath is null)
        {
            messages.Add($"{container.Name}: no compose file available; recreate it from " +
                         $"{container.InspectRelativePath} by hand.");
            return (null, null, null, false);
        }

        if (options.SourceOnHost)
        {
            var onHost = IRemoteFileSystem.Combine(root, container.ComposeRelativePath);
            if (!await remote.FileExistsAsync(onHost, ct).ConfigureAwait(false))
            {
                messages.Add($"{container.Name}: compose file missing from the backup set.");
                return (null, null, null, false);
            }

            return (onHost, null, container.ComposeService, false);
        }

        var localCompose = Path.Combine(root, container.ComposeRelativePath);
        if (!File.Exists(localCompose))
        {
            messages.Add($"{container.Name}: compose file missing from the backup set.");
            return (null, null, null, false);
        }

        var remoteDir = $"{RemoteStagingDir}/{SanitiseRemote(container.Name)}";
        var staged = $"{remoteDir}/docker-compose.yml";
        await docker.EnsureDirectoryAsync(remoteDir, ct).ConfigureAwait(false);

        var yaml = await File.ReadAllTextAsync(localCompose, ct).ConfigureAwait(false);
        await remote.WriteTextAsync(staged, yaml, ct).ConfigureAwait(false);

        return (staged, null, container.ComposeService, false);
    }

    /// <summary>
    /// Creates any user-defined network the container was on but the host does not have. Without
    /// this, compose would either fail on the external declaration or quietly place the container
    /// on its own project network, where the peers that used to reach it by name cannot.
    /// </summary>
    private async Task EnsureNetworksAsync(ContainerBackup container, BackupManifest manifest, List<RestoreNote> messages, CancellationToken ct)
    {
        foreach (var network in container.Networks.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var exists = await ssh.RunAsync(
                $"docker network inspect {SshSession.Quote(network)} >/dev/null 2>&1 && echo yes || echo no",
                Elevate, ct).ConfigureAwait(false);

            if (exists.StdOut.Trim() == "yes")
            {
                log.LogDebug("Network {Network} already exists", network);
                continue;
            }

            var definition = manifest.Networks.FirstOrDefault(n =>
                string.Equals(n.Name, network, StringComparison.Ordinal));

            var command = definition is null
                ? $"docker network create {SshSession.Quote(network)}"
                : BuildNetworkCreateCommand(definition);

            if (definition is null)
            {
                log.LogWarning(
                    "Creating network {Network} with default settings — the backup has no definition for it, " +
                    "so its addressing may differ from the original", network);
            }
            else
            {
                log.LogInformation("Creating network {Network} as it was: {Command}", network, command);
            }

            var created = await ssh.RunAsync(command, Elevate, ct).ConfigureAwait(false);

            if (created.Success)
            {
                messages.Add($"{container.Name}: created missing network {network}.");
            }
            else
            {
                // Not fatal on its own; compose will report it far more clearly if it matters.
                log.LogWarning("Could not create network {Network}: {Error}", network, created.StdErr);
                messages.Add($"{container.Name}: could not create network {network} — {created.StdErr.Trim()}");
            }
        }
    }

    /// <summary>
    /// Rebuilds a network as it was, addressing included. Compose's own labels are dropped: leaving
    /// them on a network compose did not create is what makes it refuse to adopt it later.
    /// </summary>
    private static string BuildNetworkCreateCommand(NetworkDefinition definition)
    {
        var sb = new StringBuilder("docker network create");
        sb.Append(" --driver ").Append(SshSession.Quote(definition.Driver));

        if (definition.Internal) sb.Append(" --internal");
        if (definition.Attachable) sb.Append(" --attachable");
        if (definition.EnableIPv6) sb.Append(" --ipv6");

        foreach (var subnet in definition.Subnets)
        {
            sb.Append(" --subnet ").Append(SshSession.Quote(subnet.Subnet));

            if (subnet.Gateway is { Length: > 0 } gateway)
                sb.Append(" --gateway ").Append(SshSession.Quote(gateway));

            if (subnet.IpRange is { Length: > 0 } range)
                sb.Append(" --ip-range ").Append(SshSession.Quote(range));
        }

        foreach (var option in definition.Options)
            sb.Append(" --opt ").Append(SshSession.Quote($"{option.Key}={option.Value}"));

        foreach (var label in definition.Labels
                     .Where(l => !l.Key.StartsWith("com.docker.compose.", StringComparison.Ordinal)))
        {
            sb.Append(" --label ").Append(SshSession.Quote($"{label.Key}={label.Value}"));
        }

        sb.Append(' ').Append(SshSession.Quote(definition.Name));
        return sb.ToString();
    }

    private async Task<bool> ContainerExistsAsync(string name, CancellationToken ct)
    {
        var result = await ssh.RunAsync(
            $"docker ps -a --format '{{{{.Names}}}}' | grep -Fx {SshSession.Quote(name)} || true",
            Elevate, ct).ConfigureAwait(false);
        return result.StdOut.Trim().Length > 0;
    }

    /// <summary>Container Manager ships compose v2 as a docker plugin; older hosts have the v1 script.</summary>
    private async Task<string> ResolveComposeCommandAsync(CancellationToken ct)
    {
        if (_composeCommand is not null) return _composeCommand;

        var v2 = await ssh.RunAsync("docker compose version", Elevate, ct).ConfigureAwait(false);
        if (v2.Success)
        {
            _composeCommand = "docker compose";
        }
        else
        {
            var v1 = await ssh.RunAsync("docker-compose version", Elevate, ct).ConfigureAwait(false);
            _composeCommand = v1.Success
                ? "docker-compose"
                : throw new SshCommandException(
                    "Neither `docker compose` nor `docker-compose` is available on the host, so containers " +
                    "cannot be recreated automatically. Volume data can still be restored.");
        }

        log.LogInformation("Using compose command: {Command}", _composeCommand);
        return _composeCommand;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Rewrites a path as one container saw it into the host path behind it, using the bind mounts
    /// recorded in the backup. Longest destination wins, so a nested mount beats its parent.
    /// </summary>
    private static string? TranslateThroughBindMounts(string containerPath, BackupManifest manifest)
    {
        var mounts = manifest.Containers
            .SelectMany(c => c.Archives)
            .Where(a => a.Kind == ArchiveKind.Bind)
            .Where(a => a.Destination.Length > 1 && a.Source.Length > 0)
            .Select(a => (Destination: a.Destination.TrimEnd('/'), Source: a.Source.TrimEnd('/')))
            .Distinct()
            .OrderByDescending(m => m.Destination.Length);

        foreach (var (destination, source) in mounts)
        {
            if (!containerPath.StartsWith(destination + "/", StringComparison.Ordinal)) continue;
            return source + containerPath[destination.Length..];
        }

        return null;
    }

    private static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private static string SanitiseRemote(string value)
    {
        var cleaned = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray())
            .Trim('-');
        return cleaned.Length == 0 ? "restored" : cleaned.ToLowerInvariant();
    }
}
