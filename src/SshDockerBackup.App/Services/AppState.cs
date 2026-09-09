using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SshDockerBackup.App.ViewModels;
using SshDockerBackup.Core.Docker;

namespace SshDockerBackup.App.Services;

/// <summary>
/// Shared state between tabs: one connection, one container list. Backup and Restore read what
/// Containers loaded rather than each fetching their own copy.
/// </summary>
public sealed partial class AppState : ObservableObject
{
    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string ConnectionSummary { get; set; } = "Not connected";

    [ObservableProperty]
    public partial DockerHostInfo? HostInfo { get; set; }

    /// <summary>True while a backup or restore is running; used to disable conflicting actions.</summary>
    [ObservableProperty]
    public partial bool IsOperationRunning { get; set; }

    public ObservableCollection<ContainerItemViewModel> Containers { get; } = [];

    public IReadOnlyList<ContainerItemViewModel> SelectedContainers =>
        [.. Containers.Where(c => c.IsSelected)];
}
