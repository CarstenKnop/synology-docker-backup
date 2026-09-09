using System.Windows;
using SshDockerBackup.App.ViewModels;

namespace SshDockerBackup.App.Views;

public partial class ReportDialog : Window
{
    public ReportDialog(ReportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnContinue(object sender, RoutedEventArgs e) => DialogResult = true;
}
