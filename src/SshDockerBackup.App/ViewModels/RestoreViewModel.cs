using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SshDockerBackup.App.Services;
using SshDockerBackup.Core.Backup;
using SshDockerBackup.Core.Models;
using SshDockerBackup.Core.Reporting;

namespace SshDockerBackup.App.ViewModels;

public sealed partial class RestoreItemViewModel(ContainerBackup model) : ObservableObject
{
    public ContainerBackup Model { get; } = model;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    public string Name => Model.Name;
    public string Image => Model.Image;

    /// <summary>Run state captured at backup time; restore puts the container back into it.</summary>
    public bool WasRunning => Model.WasRunning;

    public string WasRunningText => Model.WasRunning ? "running" : "stopped";

    public int ArchiveCount => Model.Archives.Count(a => a.Succeeded);

    public long TotalBytes => Model.Archives.Where(a => a.Succeeded).Sum(a => a.SizeBytes);

    public string SizeText => BackupProgress.FormatBytes(TotalBytes);

    public string ContentSummary
    {
        get
        {
            var volumes = Model.Archives.Count(a => a.Kind == ArchiveKind.Volume && a.Succeeded);
            var binds = Model.Archives.Count(a => a.Kind == ArchiveKind.Bind && a.Succeeded);
            var images = Model.Archives.Count(a => a.Kind == ArchiveKind.Image && a.Succeeded);

            var parts = new List<string>();
            if (volumes > 0) parts.Add($"{volumes} volume{(volumes == 1 ? "" : "s")}");
            if (binds > 0) parts.Add($"{binds} bind mount{(binds == 1 ? "" : "s")}");
            if (images > 0) parts.Add($"{images} image{(images == 1 ? "" : "s")}");
            if (Model.ComposeRelativePath is not null) parts.Add("compose file");

            return parts.Count == 0 ? "config only" : string.Join(", ", parts);
        }
    }

    public bool HasCompose => Model.ComposeRelativePath is not null;
}

/// <summary>A host folder captured in the backup, such as a project directory.</summary>
public sealed partial class ExtraPathItemViewModel(ArchiveEntry model, IReadOnlyList<string> owners)
    : ObservableObject
{
    public ArchiveEntry Model { get; } = model;

    /// <summary>
    /// Containers whose compose file or data lives in this folder. A project directory almost always
    /// has some: it holds the compose file that defines the container, so writing it back while its
    /// container is not being restored overwrites live files for no reason.
    /// </summary>
    public IReadOnlyList<string> Owners { get; } = owners;

    public bool IsOwned => Owners.Count > 0;

    /// <summary>
    /// A folder no container claims is the user's own choice to make. One that belongs to containers
    /// follows their ticks instead, so unticking a container cannot quietly overwrite its folder.
    /// </summary>
    public bool CanChoose => !IsOwned;

    public string OwnersText => IsOwned ? string.Join(", ", Owners) : "no container — yours to choose";

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    public string HostPath => Model.Source;
    public string SizeText => BackupProgress.FormatBytes(Model.SizeBytes);
    public string StatusText => Model.Succeeded ? "ok" : $"failed: {Model.Error}";
}

public sealed partial class RestoreViewModel : OperationViewModelBase
{
    private readonly IRestoreService _restore;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<RestoreViewModel> _log;
    private readonly AppSettings _settings;

    private BackupManifest? _manifest;
    private CancellationTokenSource? _cancellation;

    public RestoreViewModel(
        IRestoreService restore,
        AppState state,
        IDialogService dialogs,
        ISettingsStore settingsStore,
        ILogger<RestoreViewModel> log)
    {
        _restore = restore;
        State = state;
        _dialogs = dialogs;
        _settingsStore = settingsStore;
        _log = log;

        _settings = settingsStore.Load();
        SourceOnHost = _settings.RestoreSourceOnHost;
        BackupFolder = (SourceOnHost ? _settings.LastHostRestoreFolder : _settings.LastRestoreFolder) ?? "";

        ApplySavedOptions();
    }

    /// <summary>
    /// Restores the remembered choices. ReplaceExisting is not among them: it force-removes running
    /// containers, and a destructive option carried over from a previous session is a trap.
    /// </summary>
    private void ApplySavedOptions()
    {
        RestoreVolumeData = _settings.RestoreVolumeData;
        RestoreHostFolders = _settings.RestoreHostFolders;
        RecreateContainers = _settings.RecreateContainers;
        LoadImages = _settings.RestoreLoadImages;
        VerifyChecksums = _settings.VerifyChecksums;
        PreserveRunningState = _settings.PreserveRunningState;
        RegisterSynologyProjects = _settings.RegisterSynologyProjects;
        ReplaceExisting = false;
    }

    private void SaveOptions()
    {
        _settings.RestoreVolumeData = RestoreVolumeData;
        _settings.RestoreHostFolders = RestoreHostFolders;
        _settings.RecreateContainers = RecreateContainers;
        _settings.RestoreLoadImages = LoadImages;
        _settings.VerifyChecksums = VerifyChecksums;
        _settings.PreserveRunningState = PreserveRunningState;
        _settings.RegisterSynologyProjects = RegisterSynologyProjects;

        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private void ResetToRecommended() => ResetToRecommendedDefaults();

    /// <summary>
    /// Puts every option back to the combination that produces a complete restore, and ticks
    /// everything in the loaded backup so nothing is left behind by an old selection.
    /// </summary>
    public void ResetToRecommendedDefaults()
    {
        RestoreVolumeData = true;
        RestoreHostFolders = true;
        RecreateContainers = true;
        LoadImages = true;
        VerifyChecksums = true;
        PreserveRunningState = true;
        RegisterSynologyProjects = true;
        ReplaceExisting = false;

        foreach (var item in Items) item.IsSelected = true;

        // Unowned folders are ticked outright; owned ones are derived from the containers, which are
        // all ticked by now, so this lands on "everything" either way -- by the same rule as always.
        foreach (var item in ExtraPathItems.Where(f => f.CanChoose)) item.IsSelected = true;
        SyncHostFolderSelection();

        SaveOptions();
        HasError = false;
        StatusMessage = "Restore options reset to the recommended defaults; everything is ticked.";
    }

    /// <summary>Each mode remembers its own last folder, so switching back and forth loses neither.</summary>
    partial void OnSourceOnHostChanged(bool value)
    {
        BackupFolder = (value ? _settings.LastHostRestoreFolder : _settings.LastRestoreFolder) ?? "";
        HasManifest = false;
        Items.Clear();
        ExtraPathItems.Clear();
        OnPropertyChanged(nameof(HasExtraPaths));
        ManifestSummary = "No backup loaded.";
    }

    private void OnRestoreItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RestoreItemViewModel.IsSelected)) SyncHostFolderSelection();
    }

    /// <summary>
    /// Makes each host folder follow the containers it belongs to. Without this the two lists are
    /// independent, and unticking a container still writes its project folder back over whatever is
    /// on the host now — which is exactly how a live folder gets overwritten by a stale copy.
    /// Folders no container claims are left alone, because nothing else can decide them.
    /// </summary>
    private void SyncHostFolderSelection()
    {
        var selected = Items.Where(i => i.IsSelected).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var folder in ExtraPathItems.Where(f => f.IsOwned))
            folder.IsSelected = folder.Owners.Any(selected.Contains);
    }

    /// <summary>
    /// Containers that live in this folder: ones whose compose working directory is the folder, and
    /// ones with a bind mount inside it.
    /// </summary>
    private static List<string> FindOwners(ArchiveEntry extra, BackupManifest manifest)
    {
        var folder = extra.Source.TrimEnd('/');
        if (folder.Length == 0) return [];

        return [.. manifest.Containers
            .Where(c => Covers(folder, c.ComposeWorkingDir) ||
                        c.Archives.Any(a => a.Kind == ArchiveKind.Bind && Covers(folder, a.Source)))
            .Select(c => c.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>True when the folder is the path itself, or contains it.</summary>
    private static bool Covers(string folder, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var candidate = path.TrimEnd('/');
        return string.Equals(candidate, folder, StringComparison.Ordinal) ||
               candidate.StartsWith(folder + "/", StringComparison.Ordinal);
    }

    public AppState State { get; }

    public ObservableCollection<RestoreItemViewModel> Items { get; } = [];
    public ObservableCollection<ExtraPathItemViewModel> ExtraPathItems { get; } = [];

    public bool HasExtraPaths => ExtraPathItems.Count > 0;

    /// <summary>Carries the count, so the collapsed panel still advertises what is inside it.</summary>
    public string ExtraPathsHeader => ExtraPathItems.Count == 1
        ? "Host folders in this backup — 1 project directory"
        : $"Host folders in this backup — {ExtraPathItems.Count} project directories";

    [ObservableProperty] public partial string BackupFolder { get; set; } = "";

    /// <summary>The backup set lives on the NAS (a USB drive, say) rather than on this PC.</summary>
    [ObservableProperty] public partial bool SourceOnHost { get; set; }
    [ObservableProperty] public partial string ManifestSummary { get; set; } = "No backup loaded.";
    [ObservableProperty] public partial bool HasManifest { get; set; }

    [ObservableProperty] public partial bool RestoreVolumeData { get; set; } = true;
    [ObservableProperty] public partial bool RestoreHostFolders { get; set; } = true;
    [ObservableProperty] public partial bool RecreateContainers { get; set; } = true;
    [ObservableProperty] public partial bool LoadImages { get; set; } = true;
    [ObservableProperty] public partial bool VerifyChecksums { get; set; } = true;
    [ObservableProperty] public partial bool ReplaceExisting { get; set; }
    [ObservableProperty] public partial bool PreserveRunningState { get; set; } = true;
    [ObservableProperty] public partial bool RegisterSynologyProjects { get; set; } = true;

    [RelayCommand]
    private async Task BrowseAndLoadAsync()
    {
        string? folder;

        if (SourceOnHost)
        {
            if (!State.IsConnected)
            {
                Fail("Connect on the Connection tab first — browsing NAS folders needs the SSH session.");
                return;
            }

            folder = _dialogs.PickRemoteFolder(string.IsNullOrWhiteSpace(BackupFolder) ? null : BackupFolder);
        }
        else
        {
            folder = _dialogs.PickFolder("Select a backup folder (the one containing manifest.json)", BackupFolder);
        }

        if (folder is null) return;

        BackupFolder = folder;
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(BackupFolder))
        {
            Fail("Choose a backup folder first.");
            return;
        }

        if (SourceOnHost && !State.IsConnected)
        {
            Fail("Connect first — the backup is on the NAS, so reading it needs the SSH session.");
            return;
        }

        if (!SourceOnHost && !Directory.Exists(BackupFolder))
        {
            Fail("That folder does not exist on this PC.");
            return;
        }

        try
        {
            HasError = false;
            _manifest = await _restore.LoadManifestAsync(BackupFolder, SourceOnHost).ConfigureAwait(true);

            foreach (var stale in Items) stale.PropertyChanged -= OnRestoreItemChanged;
            Items.Clear();

            foreach (var container in _manifest.Containers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new RestoreItemViewModel(container);
                item.PropertyChanged += OnRestoreItemChanged;
                Items.Add(item);
            }

            ExtraPathItems.Clear();
            foreach (var extra in _manifest.ExtraPaths.Where(e => e.Succeeded)
                         .OrderBy(e => e.Source, StringComparer.Ordinal))
                ExtraPathItems.Add(new ExtraPathItemViewModel(extra, FindOwners(extra, _manifest)));

            SyncHostFolderSelection();

            OnPropertyChanged(nameof(HasExtraPaths));
            OnPropertyChanged(nameof(ExtraPathsHeader));

            HasManifest = true;
            var folderNote = ExtraPathItems.Count > 0 ? $"  ·  {ExtraPathItems.Count} host folder(s)" : "";
            ManifestSummary =
                $"{_manifest.Containers.Count} containers{folderNote}  ·  " +
                $"{BackupProgress.FormatBytes(_manifest.TotalBytes)}  ·  " +
                $"taken {_manifest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ·  from {_manifest.HostDescription}";

            StatusMessage = "Backup loaded. Tick what you want to restore.";

            if (SourceOnHost) _settings.LastHostRestoreFolder = BackupFolder;
            else _settings.LastRestoreFolder = BackupFolder;

            _settings.RestoreSourceOnHost = SourceOnHost;
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not load backup from {Folder}", BackupFolder);
            HasManifest = false;
            Items.Clear();
            ManifestSummary = "No backup loaded.";
            Fail(ex.Message);
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Items) item.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in Items) item.IsSelected = false;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_manifest is null)
        {
            Fail("Load a backup folder first.");
            return;
        }

        if (!State.IsConnected)
        {
            Fail("Not connected. Connect on the Connection tab first.");
            return;
        }

        var selected = Items.Where(i => i.IsSelected).Select(i => i.Model).ToList();
        var selectedExtras = RestoreHostFolders
            ? ExtraPathItems.Where(i => i.IsSelected).Select(i => i.Model).ToList()
            : [];

        if (selected.Count == 0 && selectedExtras.Count == 0)
        {
            Fail("Nothing is ticked.");
            return;
        }

        var options = new RestoreOptions
        {
            BackupRoot = BackupFolder,
            SourceOnHost = SourceOnHost,
            RestoreVolumeData = RestoreVolumeData,
            RecreateContainers = RecreateContainers,
            LoadImages = LoadImages,
            VerifyChecksums = VerifyChecksums,
            RemoveExistingContainers = ReplaceExisting,
            PreserveRunningState = PreserveRunningState,
            RestoreExtraPaths = RestoreHostFolders,
            RegisterSynologyProjects = RegisterSynologyProjects,
        };

        SaveOptions();

        // Check image availability and internet before anything is written, so a restore that
        // cannot possibly finish is caught here rather than halfway through.
        RestorePreflight? preflight = null;
        try
        {
            StatusMessage = "Checking images and internet access on the NAS...";
            preflight = await _restore.PreflightAsync(selected, options).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restore pre-flight failed; continuing without it");
        }
        finally
        {
            StatusMessage = "";
        }

        // Shown every time rather than only when something is destructive: the reader should see
        // what is about to be written, what will start, and what a restore cannot do for them.
        var briefing = ReportBuilders.BeforeRestore(
            _manifest, selected, selectedExtras, options, State.ConnectionSummary, preflight);

        if (!_dialogs.ShowReport(briefing, isConfirmation: true)) return;

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        State.IsOperationRunning = true;
        HasError = false;
        StatusMessage = "";
        ResetProgress();

        var progress = new Progress<BackupProgress>(ApplyProgress);

        try
        {
            var outcome = await _restore
                .RestoreAsync(_manifest, selected, selectedExtras, options, progress, _cancellation.Token)
                .ConfigureAwait(true);

            ProgressPercent = 100;
            IsIndeterminate = false;

            HasError = outcome.Failed > 0;
            StatusMessage = outcome.Failed == 0
                ? $"Restored {outcome.Restored} container(s)."
                : $"Restored {outcome.Restored}, failed {outcome.Failed}. See the Log tab.";

            if (outcome.Messages.Count > 0)
            {
                _log.LogInformation("Restore notes:{NewLine}{Detail}",
                    Environment.NewLine, string.Join(Environment.NewLine, outcome.Messages));
            }

            _dialogs.ShowReport(
                ReportBuilders.AfterRestore(outcome, selected, options), isConfirmation: false);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Restore cancelled by the user");
            StatusMessage = "Cancelled. Anything already written to the host was left in place.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed");
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

    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (SourceOnHost)
        {
            _dialogs.ShowInfo("Backup location", $"This set is on the NAS at:{Environment.NewLine}{BackupFolder}");
            return;
        }

        if (Directory.Exists(BackupFolder)) _dialogs.OpenInExplorer(BackupFolder);
    }

    private void Fail(string message)
    {
        HasError = true;
        StatusMessage = message;
    }
}
