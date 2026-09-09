using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SshDockerBackup.Core.Backup;
using SshDockerBackup.Core.Remote;

namespace SshDockerBackup.App.ViewModels;

/// <summary>A volume offered as a starting point, with its free space already worked out.</summary>
public sealed record VolumeChoice(RemoteVolume Volume)
{
    public string Path => Volume.Path;

    /// <summary>
    /// Last path segment, which for a USB drive is the share name File Station shows, such as
    /// "usbshare1-1". Leading with it makes the list recognisable rather than a wall of paths.
    /// </summary>
    public string Name => Volume.Path.TrimEnd('/').Split('/').LastOrDefault() is { Length: > 0 } last
        ? last
        : Volume.Path;

    public string Display =>
        $"{Name}   ({Volume.Path})   {BackupProgress.FormatBytes(Volume.FreeBytes)} free of " +
        $"{BackupProgress.FormatBytes(Volume.TotalBytes)}   [{Volume.Kind}]";
}

/// <summary>
/// Browses folders on the Docker host so a backup can be written to, or read from, a USB drive
/// plugged into the NAS.
/// </summary>
public sealed partial class RemoteFolderViewModel(
    IRemoteFileSystem remote,
    ILogger<RemoteFolderViewModel> log) : ObservableObject
{
    public ObservableCollection<VolumeChoice> Volumes { get; } = [];
    public ObservableCollection<RemoteEntry> Entries { get; } = [];

    [ObservableProperty] public partial string CurrentPath { get; set; } = "/volume1";
    [ObservableProperty] public partial RemoteEntry? SelectedEntry { get; set; }
    [ObservableProperty] public partial VolumeChoice? SelectedVolume { get; set; }
    [ObservableProperty] public partial string NewFolderName { get; set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "";
    [ObservableProperty] public partial string FreeSpaceText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>Set when the user accepts; read by the caller after the dialog closes.</summary>
    public string? ChosenPath { get; private set; }

    public async Task InitialiseAsync(string? startPath)
    {
        if (!string.IsNullOrWhiteSpace(startPath)) CurrentPath = startPath;

        await LoadVolumesAsync().ConfigureAwait(true);

        // Fall back to the first volume when the remembered path has gone away, which is exactly
        // what happens when a USB drive was unplugged since last time.
        if (!await remote.DirectoryExistsAsync(CurrentPath).ConfigureAwait(true))
        {
            var fallback = Volumes.FirstOrDefault()?.Path ?? "/";
            StatusMessage = $"{CurrentPath} is not there any more; showing {fallback}.";
            CurrentPath = fallback;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task LoadVolumesAsync()
    {
        try
        {
            var volumes = await remote.ListVolumesAsync().ConfigureAwait(true);
            Volumes.Clear();

            // Removable drives first: they are usually what you are reaching for here.
            foreach (var volume in volumes.OrderByDescending(v => v.IsRemovable).ThenBy(v => v.Path, StringComparer.Ordinal))
                Volumes.Add(new VolumeChoice(volume));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not list volumes");
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var entries = await remote.ListDirectoriesAsync(CurrentPath).ConfigureAwait(true);

            Entries.Clear();
            foreach (var entry in entries) Entries.Add(entry);

            var free = await remote.GetFreeBytesAsync(CurrentPath).ConfigureAwait(true);
            FreeSpaceText = free > 0 ? $"{BackupProgress.FormatBytes(free)} free here" : "";

            if (entries.Count == 0) StatusMessage = "No sub-folders here.";
            else StatusMessage = "";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not list {Path}", CurrentPath);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedEntry is null) return;

        CurrentPath = SelectedEntry.FullPath;
        SelectedEntry = null;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoUpAsync()
    {
        var trimmed = CurrentPath.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        if (index <= 0)
        {
            CurrentPath = "/";
        }
        else
        {
            CurrentPath = trimmed[..index];
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoToVolumeAsync()
    {
        if (SelectedVolume is null) return;

        CurrentPath = SelectedVolume.Path;
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        var name = NewFolderName.Trim().Trim('/');
        if (name.Length == 0)
        {
            StatusMessage = "Type a name for the new folder first.";
            return;
        }

        if (name.Contains('/'))
        {
            StatusMessage = "Use a single folder name, not a path.";
            return;
        }

        var target = IRemoteFileSystem.Combine(CurrentPath, name);

        try
        {
            if (await remote.DirectoryExistsAsync(target).ConfigureAwait(true))
            {
                StatusMessage = "That folder already exists; opening it.";
            }
            else
            {
                await remote.CreateDirectoryAsync(target).ConfigureAwait(true);
                StatusMessage = $"Created {target}";
            }

            NewFolderName = "";
            CurrentPath = target;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not create {Path}", target);
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Accepts the folder currently shown. Returns false when it cannot be used.</summary>
    public async Task<bool> TryAcceptAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath) || !CurrentPath.StartsWith('/'))
        {
            StatusMessage = "Choose an absolute path on the host.";
            return false;
        }

        if (!await remote.DirectoryExistsAsync(CurrentPath).ConfigureAwait(true))
        {
            StatusMessage = "That folder does not exist. Create it first.";
            return false;
        }

        ChosenPath = CurrentPath.TrimEnd('/');
        return true;
    }
}
