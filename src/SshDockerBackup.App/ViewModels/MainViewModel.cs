using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshDockerBackup.App.Services;

namespace SshDockerBackup.App.ViewModels;

public sealed partial class MainViewModel(
    AppState state,
    ConnectionViewModel connection,
    ContainersViewModel containers,
    BackupViewModel backup,
    RestoreViewModel restore,
    LogViewModel log,
    IDialogService dialogs) : ObservableObject
{
    public AppState State { get; } = state;
    public ConnectionViewModel Connection { get; } = connection;
    public ContainersViewModel Containers { get; } = containers;
    public BackupViewModel Backup { get; } = backup;
    public RestoreViewModel Restore { get; } = restore;
    public LogViewModel Log { get; } = log;

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    public string WindowTitle => "SSH Docker Backup";

    /// <summary>
    /// One way back to the settings that produce a complete backup and a complete restore, for when
    /// options have been changed for some one-off job and nobody remembers what they were before.
    /// </summary>
    [RelayCommand]
    private void ResetSettings()
    {
        // Paragraphs are single lines: the dialog wraps them itself, and hard-wrapping as well
        // produces the ragged half-empty lines that make a dialog look thrown together.
        var confirmed = dialogs.Confirm(
            "Reset to recommended settings",
            """
            This puts every backup and restore option back to the settings that produce a complete backup and a complete restore:

               •  Every container image is saved, so a backup restores with no internet
               •  Containers are paused while their data is copied
               •  Checksums are written and verified
               •  Project folders are found and included automatically
               •  Containers come back in the state they were in, inside their original projects
               •  Nothing destructive is switched on

            Everything currently listed will be ticked.

            Your destination folder, backup folder and connection details are not changed, and neither are any host folders you added by hand or any folders you chose to leave out.

            Continue?
            """);

        if (!confirmed) return;

        Backup.ResetToRecommendedDefaults();
        Restore.ResetToRecommendedDefaults();
    }
}
