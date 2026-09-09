using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SshDockerBackup.App.Services;
using SshDockerBackup.Core.Backup;
using SshDockerBackup.Core.Docker;
using SshDockerBackup.Core.Models;

namespace SshDockerBackup.App.ViewModels;

public sealed partial class ContainersViewModel : ObservableObject
{
    private readonly IDockerService _docker;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ContainersViewModel> _log;

    public ContainersViewModel(
        IDockerService docker,
        AppState state,
        IDialogService dialogs,
        ILogger<ContainersViewModel> log)
    {
        _docker = docker;
        State = state;
        _dialogs = dialogs;
        _log = log;

        State.Containers.CollectionChanged += OnContainersChanged;
    }

    public AppState State { get; }

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Connect first, then refresh.";
    [ObservableProperty] public partial ContainerItemViewModel? SelectedContainer { get; set; }

    public int SelectedCount => State.Containers.Count(c => c.IsSelected);
    public bool CanOperate => State.IsConnected && !IsBusy;

    /// <summary>True once "Calculate sizes" has run, so the totals line is worth showing.</summary>
    [ObservableProperty]
    public partial bool HasMeasuredSizes { get; set; }

    public string TotalSizeText =>
        BackupProgress.FormatBytes(State.Containers.Sum(c => c.SizeBytes));

    public string SelectedSizeText =>
        BackupProgress.FormatBytes(State.Containers.Where(c => c.IsSelected).Sum(c => c.SizeBytes));

    private void RaiseSizeTotals()
    {
        OnPropertyChanged(nameof(TotalSizeText));
        OnPropertyChanged(nameof(SelectedSizeText));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!State.IsConnected)
        {
            StatusMessage = "Not connected.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Listing containers...";

        // The new list carries no measurements, so the totals line must not linger from last time.
        HasMeasuredSizes = false;

        try
        {
            var containers = await _docker.ListContainersAsync().ConfigureAwait(true);

            // Keep ticks across a refresh so a long selection is not lost.
            var previouslySelected = State.Containers
                .Where(c => c.IsSelected)
                .Select(c => c.Name)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var item in State.Containers)
                item.PropertyChanged -= OnItemPropertyChanged;

            State.Containers.Clear();

            foreach (var container in containers.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ContainerItemViewModel(container)
                {
                    IsSelected = previouslySelected.Contains(container.Name),
                };
                item.PropertyChanged += OnItemPropertyChanged;
                State.Containers.Add(item);
            }

            var withData = State.Containers.Count(c => c.BackupableMounts.Count > 0);
            StatusMessage = $"{State.Containers.Count} containers, {withData} with durable data.";
            _log.LogInformation("Loaded {Count} containers", State.Containers.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not list containers");
            StatusMessage = ex.Message;
            _dialogs.ShowError("Could not list containers", ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(SelectedCount));
        }
    }

    /// <summary>
    /// Runs du against every mount. Separate from refresh because on a spinning-disk NAS it can
    /// take a while, and you rarely need it just to pick containers.
    /// </summary>
    [RelayCommand]
    private async Task CalculateSizesAsync()
    {
        if (!State.IsConnected) return;

        IsBusy = true;
        try
        {
            var measured = 0;

            foreach (var item in State.Containers)
            {
                var mounts = item.BackupableMounts;
                if (mounts.Count == 0)
                {
                    item.ApplySize(0);
                    continue;
                }

                StatusMessage = $"Measuring {item.Name}...";
                long total = 0;

                foreach (var mount in mounts)
                {
                    var path = mount.Source;
                    if (mount.Kind == MountKind.Volume)
                    {
                        var resolved = await _docker.GetVolumeMountpointAsync(mount.Name).ConfigureAwait(true);
                        if (!string.IsNullOrWhiteSpace(resolved)) path = resolved;
                    }

                    if (string.IsNullOrWhiteSpace(path)) continue;
                    total += await _docker.GetPathSizeBytesAsync(path).ConfigureAwait(true);
                }

                item.ApplySize(total);
                if (total > 0) measured++;

                // Keep the running totals live rather than only at the end, since this is slow.
                RaiseSizeTotals();
            }

            HasMeasuredSizes = true;
            RaiseSizeTotals();

            StatusMessage =
                $"{TotalSizeText} across {measured} containers with data, " +
                $"{SelectedSizeText} in the {SelectedCount} ticked. Uncompressed — archives will be smaller.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not measure container sizes");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void SelectNone() => SetAllSelected(false);

    /// <summary>Ticks only the containers that actually have volumes or bind mounts.</summary>
    [RelayCommand]
    private void SelectWithData()
    {
        foreach (var item in State.Containers)
            item.IsSelected = item.BackupableMounts.Count > 0;

        OnPropertyChanged(nameof(SelectedCount));
        RaiseSizeTotals();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var item in State.Containers)
            item.IsSelected = selected;

        OnPropertyChanged(nameof(SelectedCount));
        RaiseSizeTotals();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ContainerItemViewModel.IsSelected)) return;

        OnPropertyChanged(nameof(SelectedCount));
        RaiseSizeTotals();
    }

    private void OnContainersChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(SelectedCount));
}
