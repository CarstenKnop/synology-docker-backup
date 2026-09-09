using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SshDockerBackup.App.ViewModels;
using SshDockerBackup.App.Views;
using SshDockerBackup.Core.Remote;
using SshDockerBackup.Core.Reporting;

namespace SshDockerBackup.App.Services;

public interface IDialogService
{
    string? PickFolder(string title, string? initialDirectory = null);
    string? PickFile(string title, string filter, string? initialDirectory = null);

    /// <summary>Browses folders on the Docker host, with the option to create one. Needs a connection.</summary>
    string? PickRemoteFolder(string? startPath);

    /// <summary>
    /// Shows a report. When <paramref name="isConfirmation"/> is true it is a decision point and the
    /// return value says whether to go ahead; otherwise it is a summary and the result is ignored.
    /// </summary>
    bool ShowReport(OperationReport report, bool isConfirmation, string? savedCopyPath = null);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    bool Confirm(string title, string message);
    void OpenInExplorer(string path);
}

public sealed class DialogService(
    IRemoteFileSystem remote,
    ILogger<RemoteFolderViewModel> remoteLogger,
    ILogger<ReportViewModel> reportLogger) : IDialogService
{
    public bool ShowReport(OperationReport report, bool isConfirmation, string? savedCopyPath = null)
    {
        var viewModel = new ReportViewModel(report, isConfirmation, reportLogger)
        {
            PreSavedPath = savedCopyPath,
        };

        var dialog = new ReportDialog(viewModel)
        {
            Owner = Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true;
    }

    public string? PickRemoteFolder(string? startPath)
    {
        var dialog = new RemoteFolderDialog(new RemoteFolderViewModel(remote, remoteLogger), startPath)
        {
            Owner = Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true ? dialog.ChosenPath : null;
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickFile(string title, string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    // All three go through the themed dialog. MessageBox ignores the app's theme, so a single one
    // of them makes the whole app look unfinished.
    public void ShowError(string title, string message) =>
        MessageDialog.Show(title, message, MessageDialogKind.Error, isQuestion: false, Owner);

    public void ShowInfo(string title, string message) =>
        MessageDialog.Show(title, message, MessageDialogKind.Information, isQuestion: false, Owner);

    public bool Confirm(string title, string message) =>
        MessageDialog.Show(title, message, MessageDialogKind.Question, isQuestion: true, Owner);

    private static Window? Owner => Application.Current?.MainWindow;

    public void OpenInExplorer(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }
}
