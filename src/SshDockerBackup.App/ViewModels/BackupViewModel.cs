using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SshDockerBackup.App.Services;
using SshDockerBackup.Core.Backup;
using SshDockerBackup.Core.Docker;
using SshDockerBackup.Core.Models;
using SshDockerBackup.Core.Reporting;

namespace SshDockerBackup.App.ViewModels;

public sealed partial class BackupViewModel : OperationViewModelBase
{
    private readonly IBackupService _backup;
    private readonly IDockerService _docker;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<BackupViewModel> _log;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _cancellation;

    public BackupViewModel(
        IBackupService backup,
        IDockerService docker,
        AppState state,
        IDialogService dialogs,
        ISettingsStore settingsStore,
        ILogger<BackupViewModel> log)
    {
        _backup = backup;
        _docker = docker;
        State = state;
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _log = log;

        _settings = settingsStore.Load();
        DestinationOnHost = _settings.BackupDestinationOnHost;
        Destination = (DestinationOnHost ? _settings.LastHostBackupDestination : _settings.LastBackupDestination) ?? "";
        StopContainers = _settings.StopContainersDuringBackup;
        ImageMode = _settings.ImageMode;
        ComputeChecksums = _settings.ComputeChecksums;

        foreach (var path in _settings.ExtraPaths)
            ExtraPaths.Add(path);

        foreach (var path in _settings.ExcludedPaths)
            ExcludedPaths.Add(path);

        // Keep the "n selected" readout live as ticks change on the Containers tab.
        State.Containers.CollectionChanged += OnContainersChanged;
    }

    private void OnContainersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in State.Containers)
        {
            item.PropertyChanged -= OnContainerItemChanged;
            item.PropertyChanged += OnContainerItemChanged;
        }

        OnPropertyChanged(nameof(SelectedCount));
    }

    private void OnContainerItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContainerItemViewModel.IsSelected))
            OnPropertyChanged(nameof(SelectedCount));
    }

    public AppState State { get; }

    [ObservableProperty] public partial string Destination { get; set; } = "";

    /// <summary>
    /// Write the backup to a folder on the NAS instead of this PC. For a USB drive attached to the
    /// NAS this is much faster: tar writes straight to the disk and nothing crosses the network.
    /// </summary>
    [ObservableProperty] public partial bool DestinationOnHost { get; set; }

    [ObservableProperty] public partial bool StopContainers { get; set; } = true;

    public string DestinationHint => DestinationOnHost
        ? "An absolute path on the NAS, for example /volumeUSB1/usbshare1/docker-backups."
        : "Put this somewhere other than the NAS you are backing up. An external drive or this PC is the point.";

    /// <summary>Each mode remembers its own last folder, so switching back and forth loses neither.</summary>
    partial void OnDestinationOnHostChanged(bool value)
    {
        Destination = (value ? _settings.LastHostBackupDestination : _settings.LastBackupDestination) ?? "";
        OnPropertyChanged(nameof(DestinationHint));
    }
    [ObservableProperty] public partial bool ComputeChecksums { get; set; } = true;
    [ObservableProperty] public partial ImageBackupMode ImageMode { get; set; } = ImageBackupMode.All;
    [ObservableProperty] public partial string? LastBackupPath { get; set; }

    /// <summary>
    /// Host folders to archive regardless of any container. A project directory belongs here: the
    /// compose file, Dockerfile and .env sit next to the mounted subfolder and are mounted nowhere.
    /// </summary>
    public ObservableCollection<string> ExtraPaths { get; } = [];

    /// <summary>
    /// Host folders to leave out even when a container mounts them. The case this exists for is a
    /// media library bind-mounted into an app: hundreds of gigabytes that are not container data
    /// and belong in Hyper Backup instead. Empty by default, because leaving data out of a backup
    /// should never be something that happened without being asked for.
    /// </summary>
    public ObservableCollection<string> ExcludedPaths { get; } = [];

    [ObservableProperty] public partial string PreflightSummary { get; set; } = "";
    [ObservableProperty] public partial bool IsChecking { get; set; }

    /// <summary>
    /// Checks every source before anything is transferred. Reporting this up front beats a modal
    /// appearing forty minutes into a large backup that nobody is sitting there to answer.
    /// </summary>
    [RelayCommand]
    private async Task CheckSourcesAsync()
    {
        if (!State.IsConnected)
        {
            PreflightSummary = "Connect on the Connection tab first.";
            return;
        }

        IsChecking = true;
        PreflightSummary = "Measuring sources on the host...";

        try
        {
            var report = await _backup
                .PreflightAsync([.. State.SelectedContainers.Select(c => c.Model)], BuildOptions())
                .ConfigureAwait(true);

            PreflightSummary = Describe(report);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pre-flight check failed");
            PreflightSummary = ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// Leads with the space question, which is the one that decides whether to start at all, and
    /// only then lists what would be skipped.
    /// </summary>
    private static string Describe(PreflightReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"About {BackupProgress.FormatBytes(report.EstimatedBytes)} to archive, " +
                      "measured uncompressed — the archive will be this size or smaller.");

        if (report.FreeSpaceKnown)
        {
            sb.AppendLine($"Destination {report.DestinationDescription} has " +
                          $"{BackupProgress.FormatBytes(report.DestinationFreeBytes)} free.");

            if (report.MightNotFit)
            {
                sb.AppendLine();
                sb.AppendLine("*** That may not fit. Compression could still save it if the data is mostly");
                sb.AppendLine("    text or databases, but photos and video will not shrink. ***");
            }
        }
        else
        {
            sb.AppendLine($"Free space at {report.DestinationDescription} could not be determined " +
                          "(normal for a network path).");
        }

        sb.AppendLine();

        if (!report.HasIssues)
        {
            sb.Append($"All {report.SourcesChecked} sources are present and archivable.");
            return sb.ToString();
        }

        var missing = report.Missing.Count();
        var excluded = report.Issues.Count(i => i.Kind == SourceIssueKind.Excluded);
        var other = report.Issues.Count - missing - excluded;

        var reasons = new List<string>();
        if (excluded > 0) reasons.Add($"{excluded} excluded by you");
        if (missing > 0) reasons.Add($"{missing} missing from the host");
        if (other > 0) reasons.Add($"{other} host plumbing such as docker.sock");

        sb.AppendLine($"{report.SourcesChecked} sources checked, {report.Issues.Count} will be skipped " +
                      $"({string.Join(", ", reasons)}):");

        foreach (var issue in report.Issues.Take(12))
            sb.AppendLine($"  {issue.Owner}: {issue.Source} — {issue.Reason}");

        if (report.Issues.Count > 12)
            sb.AppendLine($"  ...and {report.Issues.Count - 12} more.");

        return sb.ToString().TrimEnd();
    }

    [RelayCommand]
    private void ResetToRecommended() => ResetToRecommendedDefaults();

    /// <summary>
    /// Puts every option back to the combination that produces a complete backup, and ticks every
    /// container. The destination is left alone — that is where your disk is, not a preference.
    /// Host folders are left alone too: they are content you chose, and the automatic scan adds to
    /// them rather than replacing them.
    /// </summary>
    public void ResetToRecommendedDefaults()
    {
        StopContainers = true;
        ComputeChecksums = true;
        ImageMode = ImageBackupMode.All;

        foreach (var item in State.Containers) item.IsSelected = true;
        OnPropertyChanged(nameof(SelectedCount));

        _settings.StopContainersDuringBackup = true;
        _settings.ComputeChecksums = true;
        _settings.ImageMode = ImageBackupMode.All;
        _settingsStore.Save(_settings);

        PreflightSummary = "";
        HasError = false;
        StatusMessage = State.Containers.Count > 0
            ? $"Backup options reset to the recommended defaults; all {State.Containers.Count} containers ticked."
            : "Backup options reset to the recommended defaults.";
    }

    private BackupOptions BuildOptions() => new()
    {
        DestinationRoot = Destination,
        DestinationOnHost = DestinationOnHost,
        StopContainersDuringBackup = StopContainers,
        IncludeVolumes = true,
        ImageMode = ImageMode,
        ComputeChecksums = ComputeChecksums,
        ExtraPaths = [.. ExtraPaths],
        ExcludedPaths = [.. ExcludedPaths],
    };

    [ObservableProperty] public partial string NewExtraPath { get; set; } = "";
    [ObservableProperty] public partial string? SelectedExtraPath { get; set; }
    [ObservableProperty] public partial string ExtraPathsStatus { get; set; } = "";

    public int SelectedCount => State.SelectedContainers.Count;

    [RelayCommand]
    private void AddExtraPath()
    {
        var path = NewExtraPath.Trim().TrimEnd('/');

        if (path.Length == 0) return;
        if (!path.StartsWith('/'))
        {
            ExtraPathsStatus = "Enter an absolute path on the NAS, for example /volume1/docker/myapp";
            return;
        }

        if (ExtraPaths.Contains(path, StringComparer.Ordinal))
        {
            ExtraPathsStatus = "Already in the list.";
            return;
        }

        ExtraPaths.Add(path);
        NewExtraPath = "";
        ExtraPathsStatus = "";
        SaveExtraPaths();
    }

    [RelayCommand]
    private void RemoveExtraPath()
    {
        if (SelectedExtraPath is null) return;

        ExtraPaths.Remove(SelectedExtraPath);
        SelectedExtraPath = null;
        SaveExtraPaths();
    }

    [ObservableProperty] public partial string NewExcludedPath { get; set; } = "";
    [ObservableProperty] public partial string? SelectedExcludedPath { get; set; }
    [ObservableProperty] public partial string ExcludedPathsStatus { get; set; } = "";

    [RelayCommand]
    private void AddExcludedPath()
    {
        var path = NewExcludedPath.Trim().TrimEnd('/');

        if (path.Length == 0) return;
        if (!path.StartsWith('/'))
        {
            ExcludedPathsStatus = "Enter an absolute path on the NAS, for example /volume1/photo";
            return;
        }

        if (ExcludedPaths.Contains(path, StringComparer.Ordinal))
        {
            ExcludedPathsStatus = "Already in the list.";
            return;
        }

        ExcludedPaths.Add(path);
        NewExcludedPath = "";
        ExcludedPathsStatus = "Excluded. This folder and everything under it will be left out.";
        SaveExcludedPaths();
    }

    [RelayCommand]
    private void RemoveExcludedPath()
    {
        if (SelectedExcludedPath is null) return;

        ExcludedPaths.Remove(SelectedExcludedPath);
        SelectedExcludedPath = null;
        ExcludedPathsStatus = "";
        SaveExcludedPaths();
    }

    [RelayCommand]
    private void BrowseExcludedPath()
    {
        if (!State.IsConnected)
        {
            ExcludedPathsStatus = "Connect first.";
            return;
        }

        var picked = _dialogs.PickRemoteFolder(string.IsNullOrWhiteSpace(NewExcludedPath) ? null : NewExcludedPath);
        if (picked is not null) NewExcludedPath = picked;
    }

    /// <summary>
    /// Climbs from each bind mount looking for a compose file or Dockerfile. That finds the project
    /// directories, which mount-following backup never sees.
    /// </summary>
    [RelayCommand]
    private async Task SuggestProjectFoldersAsync()
    {
        if (!State.IsConnected)
        {
            ExtraPathsStatus = "Connect first.";
            return;
        }

        var candidates = State.Containers
            .SelectMany(c => c.BackupableMounts)
            .Where(m => m.Kind == MountKind.Bind)
            .Select(m => m.Source)
            .ToList();

        // A compose project's own folder is mounted into nothing, so climbing from bind mounts
        // never reaches it. The container's labels name the file outright, so use those too.
        candidates.AddRange(State.Containers
            .Select(c => c.Model.ComposeConfigFile)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!));

        if (candidates.Count == 0)
        {
            ExtraPathsStatus = "Nothing to scan. Refresh the Containers tab first.";
            return;
        }

        try
        {
            ExtraPathsStatus = "Scanning...";
            var found = await _docker.FindProjectFoldersAsync(candidates).ConfigureAwait(true);

            var added = 0;
            foreach (var folder in found.Where(f => !ExtraPaths.Contains(f, StringComparer.Ordinal)))
            {
                ExtraPaths.Add(folder);
                added++;
            }

            if (added > 0) SaveExtraPaths();

            ExtraPathsStatus = found.Count == 0
                ? "No compose files or Dockerfiles found near the bind mounts."
                : $"Found {found.Count} project folder(s), added {added} new.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Project folder scan failed");
            ExtraPathsStatus = ex.Message;
        }
    }

    private void SaveExtraPaths()
    {
        _settings.ExtraPaths = [.. ExtraPaths];
        _settingsStore.Save(_settings);
    }

    private void SaveExcludedPaths()
    {
        _settings.ExcludedPaths = [.. ExcludedPaths];
        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private void BrowseDestination()
    {
        if (DestinationOnHost)
        {
            if (!State.IsConnected)
            {
                Fail("Connect on the Connection tab first — browsing NAS folders needs the SSH session.");
                return;
            }

            var remoteFolder = _dialogs.PickRemoteFolder(string.IsNullOrWhiteSpace(Destination) ? null : Destination);
            if (remoteFolder is null) return;

            Destination = remoteFolder;
            _settings.LastHostBackupDestination = remoteFolder;
            _settings.BackupDestinationOnHost = true;
            _settingsStore.Save(_settings);
            return;
        }

        var folder = _dialogs.PickFolder("Choose where to store the backup", Destination);
        if (folder is null) return;

        Destination = folder;
        _settings.LastBackupDestination = folder;
        _settings.BackupDestinationOnHost = false;
        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private void OpenLastBackup()
    {
        if (string.IsNullOrWhiteSpace(LastBackupPath)) return;

        if (DestinationOnHost)
        {
            // Nothing for Explorer to open: the folder is on the NAS.
            _dialogs.ShowInfo("Backup location", $"The backup is on the NAS at:{Environment.NewLine}{LastBackupPath}");
            return;
        }

        _dialogs.OpenInExplorer(LastBackupPath);
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        var selected = State.SelectedContainers;

        if (!State.IsConnected)
        {
            Fail("Not connected. Connect on the Connection tab first.");
            return;
        }

        if (selected.Count == 0 && ExtraPaths.Count == 0)
        {
            Fail("Nothing to back up: no containers are ticked on the Containers tab, and no host folders are listed.");
            return;
        }

        if (string.IsNullOrWhiteSpace(Destination))
        {
            Fail("Choose a destination folder.");
            return;
        }

        if (DestinationOnHost)
        {
            if (!Destination.StartsWith('/'))
            {
                Fail("A NAS destination must be an absolute path, for example /volumeUSB1/usbshare1/docker-backups.");
                return;
            }
        }
        else if (!Directory.Exists(Destination))
        {
            Fail($"The destination folder does not exist: {Destination}");
            return;
        }

        var running = selected.Count(c => c.IsRunning);
        if (!StopContainers && running > 0)
        {
            var proceed = _dialogs.Confirm(
                "Back up without stopping the containers?",
                $"""
                 {running} of the containers you selected are running, and "stop each container while its data is copied" is switched off.

                 Copying a database while it is being written to usually produces a file that looks fine and cannot actually be restored. You would only find that out when you needed it.

                 Continue anyway?
                 """);

            if (!proceed) return;
        }

        if (DestinationOnHost) _settings.LastHostBackupDestination = Destination;
        else _settings.LastBackupDestination = Destination;

        _settings.BackupDestinationOnHost = DestinationOnHost;
        _settings.StopContainersDuringBackup = StopContainers;
        _settings.ImageMode = ImageMode;
        _settings.ComputeChecksums = ComputeChecksums;
        _settings.ExtraPaths = [.. ExtraPaths];
        _settings.ExcludedPaths = [.. ExcludedPaths];
        _settingsStore.Save(_settings);

        var options = BuildOptions();

        // Size and check everything once, before anything transfers, rather than interrupting the
        // run later or discovering halfway through that the destination is full.
        IsChecking = true;
        PreflightSummary = "Measuring sources on the host...";

        try
        {
            var report = await _backup
                .PreflightAsync([.. selected.Select(c => c.Model)], options)
                .ConfigureAwait(true);

            PreflightSummary = Describe(report);

            // Always shown, not only when something is wrong: the reader deserves to know what is
            // about to be copied, what is not covered at all, and that the backup holds secrets.
            var briefing = ReportBuilders.BeforeBackup(
                report, options, selected.Count,
                DestinationOnHost ? $"the NAS at {Destination}" : Destination);

            if (!_dialogs.ShowReport(briefing, isConfirmation: true)) return;
        }
        catch (Exception ex)
        {
            // A failed check should not block a backup that might otherwise work.
            _log.LogWarning(ex, "Pre-flight check failed; continuing anyway");
        }
        finally
        {
            IsChecking = false;
        }

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        State.IsOperationRunning = true;
        HasError = false;
        StatusMessage = "";
        ResetProgress();

        var progress = new Progress<BackupProgress>(ApplyProgress);

        try
        {
            var manifest = await _backup.BackupAsync(
                [.. selected.Select(c => c.Model)], options, progress, _cancellation.Token)
                .ConfigureAwait(true);

            var failures = manifest.FailedArchives.ToList();
            LastBackupPath = manifest.RootPath;

            var skipped = manifest.SkippedArchives.Count();

            if (failures.Count == 0)
            {
                StatusMessage = $"Done. {manifest.Containers.Count} containers, " +
                                $"{BackupProgress.FormatBytes(manifest.TotalBytes)} written." +
                                (skipped > 0 ? $" {skipped} source(s) skipped — see README.txt." : "");
            }
            else
            {
                HasError = true;
                StatusMessage = $"Finished with {failures.Count} failed item(s). " +
                                "See README.txt in the backup folder and the Log tab.";
            }

            ProgressPercent = 100;
            IsIndeterminate = false;

            // The same summary is already saved inside the backup, so point at that copy rather
            // than making the reader save a second one.
            var savedCopy = DestinationOnHost
                ? null
                : Path.Combine(manifest.RootPath, Core.Backup.BackupService.ReportFileName);

            _dialogs.ShowReport(
                ReportBuilders.AfterBackup(manifest, options), isConfirmation: false, savedCopy);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Backup cancelled by the user");
            StatusMessage = "Cancelled. Containers that were stopped have been restarted.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Backup failed");
            Fail(ex.Message);
        }
        finally
        {
            IsRunning = false;
            State.IsOperationRunning = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cancellation?.Cancel();
        StatusMessage = "Cancelling...";
    }

    public void RefreshSelectedCount() => OnPropertyChanged(nameof(SelectedCount));

    private void Fail(string message)
    {
        HasError = true;
        StatusMessage = message;
    }
}
