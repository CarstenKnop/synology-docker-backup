using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using SshDockerBackup.Core.Backup;

namespace SshDockerBackup.App.ViewModels;

/// <summary>Progress plumbing shared by the Backup and Restore pages.</summary>
public abstract partial class OperationViewModelBase : ObservableObject
{
    private readonly Stopwatch _sinceLastUpdate = Stopwatch.StartNew();
    private string _lastStage = "";

    [ObservableProperty] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string ProgressStage { get; set; } = "";
    [ObservableProperty] public partial string ProgressItem { get; set; } = "";
    [ObservableProperty] public partial string ProgressDetail { get; set; } = "";
    [ObservableProperty] public partial double ProgressPercent { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; } = true;
    [ObservableProperty] public partial string StatusMessage { get; set; } = "";
    [ObservableProperty] public partial bool HasError { get; set; }

    /// <summary>
    /// A 512 KB stream reports thousands of times a second. Redraw at most every 100 ms, but never
    /// swallow a stage change, which is the part the user is actually watching for.
    /// </summary>
    protected void ApplyProgress(BackupProgress progress)
    {
        var stageChanged = progress.Stage != _lastStage;
        if (!stageChanged && _sinceLastUpdate.ElapsedMilliseconds < 100) return;

        _sinceLastUpdate.Restart();
        _lastStage = progress.Stage;

        ProgressStage = progress.Stage;
        ProgressItem = progress.Item;

        var itemFraction = progress.ItemFraction;
        if (itemFraction is not null)
        {
            IsIndeterminate = false;
            ProgressPercent = itemFraction.Value * 100;
        }
        else if (progress.OverallFraction is { } overall && progress.BytesDone == 0)
        {
            IsIndeterminate = false;
            ProgressPercent = overall * 100;
        }
        else
        {
            IsIndeterminate = true;
        }

        ProgressDetail = progress.BytesDone > 0
            ? progress.BytesTotal is > 0
                ? $"{BackupProgress.FormatBytes(progress.BytesDone)} of ~{BackupProgress.FormatBytes(progress.BytesTotal.Value)}"
                : BackupProgress.FormatBytes(progress.BytesDone)
            : progress.ItemCount > 0
                ? $"{progress.ItemIndex} of {progress.ItemCount}"
                : "";
    }

    protected void ResetProgress()
    {
        _lastStage = "";
        ProgressStage = "";
        ProgressItem = "";
        ProgressDetail = "";
        ProgressPercent = 0;
        IsIndeterminate = true;
    }
}
