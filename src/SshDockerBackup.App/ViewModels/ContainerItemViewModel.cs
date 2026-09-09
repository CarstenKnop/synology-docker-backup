using CommunityToolkit.Mvvm.ComponentModel;
using SshDockerBackup.Core.Backup;
using SshDockerBackup.Core.Models;

namespace SshDockerBackup.App.ViewModels;

public sealed partial class ContainerItemViewModel(ContainerInfo model) : ObservableObject
{
    public ContainerInfo Model { get; } = model;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Filled in on demand by "Calculate sizes"; a du per mount is too slow for every refresh.</summary>
    [ObservableProperty]
    public partial string SizeText { get; set; } = "";

    /// <summary>
    /// Raw measured bytes behind <see cref="SizeText"/>, so totals can be summed. Uncompressed:
    /// the archives on disk will be smaller.
    /// </summary>
    public long SizeBytes { get; private set; }

    public string Name => Model.Name;
    public string Image => Model.Image;
    public string State => Model.State;
    public string Status => Model.Status;
    public string Ports => Model.Ports;
    public bool IsRunning => Model.IsRunning;

    public IReadOnlyList<MountInfo> BackupableMounts =>
        [.. Model.Mounts.Where(m => m.IsBackupable)];

    public int VolumeCount => BackupableMounts.Count(m => m.Kind == MountKind.Volume);
    public int BindCount => BackupableMounts.Count(m => m.Kind == MountKind.Bind);

    public string MountSummary => BackupableMounts.Count == 0
        ? "no durable data"
        : string.Join(", ", new[]
        {
            VolumeCount > 0 ? $"{VolumeCount} volume{(VolumeCount == 1 ? "" : "s")}" : null,
            BindCount > 0 ? $"{BindCount} bind mount{(BindCount == 1 ? "" : "s")}" : null,
        }.Where(s => s is not null));

    /// <summary>Long form for the details pane, one mount per line.</summary>
    public string MountDetail => BackupableMounts.Count == 0
        ? "This container has no named volumes or bind mounts. Only its configuration will be captured."
        : string.Join(Environment.NewLine, BackupableMounts.Select(m => "  " + m));

    public string ComposeProjectText => string.IsNullOrWhiteSpace(Model.ComposeProject)
        ? ""
        : $"compose project: {Model.ComposeProject}";

    public void ApplySize(long bytes)
    {
        SizeBytes = bytes;
        SizeText = bytes > 0 ? BackupProgress.FormatBytes(bytes) : "-";
    }
}
