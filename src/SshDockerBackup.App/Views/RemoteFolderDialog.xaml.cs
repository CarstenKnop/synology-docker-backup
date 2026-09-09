using System.Windows;
using SshDockerBackup.App.ViewModels;

namespace SshDockerBackup.App.Views;

public partial class RemoteFolderDialog : Window
{
    private readonly RemoteFolderViewModel _viewModel;

    public RemoteFolderDialog(RemoteFolderViewModel viewModel, string? startPath)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        // An async void handler that throws would take the whole app down, and this one talks to
        // the NAS: a session that dropped since the tab was opened must surface as a message here.
        Loaded += async (_, _) =>
        {
            try
            {
                await _viewModel.InitialiseAsync(startPath);
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = ex.Message;
            }
        };
    }

    public string? ChosenPath => _viewModel.ChosenPath;

    private async void OnFolderDoubleClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedEntry is null) return;
        await _viewModel.OpenSelectedCommand.ExecuteAsync(null);
    }

    private async void OnAccept(object sender, RoutedEventArgs e)
    {
        // Validation talks to the host, so the dialog only closes once the path is confirmed usable.
        if (await _viewModel.TryAcceptAsync())
            DialogResult = true;
    }
}
