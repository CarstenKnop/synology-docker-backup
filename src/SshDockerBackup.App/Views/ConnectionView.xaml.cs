using System.Windows;
using System.Windows.Controls;
using SshDockerBackup.App.ViewModels;

namespace SshDockerBackup.App.Views;

public partial class ConnectionView : UserControl
{
    public ConnectionView() => InitializeComponent();

    /// <summary>
    /// PasswordBox.Password is not a DependencyProperty, so it cannot be bound. Pushing it to the
    /// view model here keeps the value out of the visual tree and out of any binding trace.
    /// </summary>
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionViewModel vm && sender is PasswordBox box)
            vm.Password = box.Password;
    }
}
