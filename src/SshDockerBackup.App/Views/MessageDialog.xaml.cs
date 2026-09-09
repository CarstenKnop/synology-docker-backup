using System.Windows;
using System.Windows.Media;

namespace SshDockerBackup.App.Views;

public enum MessageDialogKind
{
    Information,
    Question,
    Warning,
    Error,
}

/// <summary>
/// A themed replacement for MessageBox. The Win32 one ignores the app's theme entirely, which makes
/// a dark-themed app look unfinished the moment anything needs saying.
/// </summary>
public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    public string DialogTitle { get; private set; } = "";

    /// <summary>
    /// Shows the dialog modally. Returns true when the primary button was pressed, which for a
    /// question means Yes and otherwise simply means acknowledged.
    /// </summary>
    public static bool Show(
        string title,
        string message,
        MessageDialogKind kind,
        bool isQuestion,
        Window? owner)
    {
        var dialog = new MessageDialog
        {
            DialogTitle = title,
            Owner = owner,
        };

        dialog.Heading.Text = title;
        dialog.Message.Text = message;

        var (glyph, brush) = Decoration(kind);
        dialog.Glyph.Text = glyph;
        dialog.Glyph.Foreground = brush;

        dialog.PrimaryButton.Content = isQuestion ? "Yes" : "OK";

        if (isQuestion)
        {
            dialog.SecondaryButton.Content = "No";
            dialog.SecondaryButton.Visibility = Visibility.Visible;
        }

        // A dialog with no owner would otherwise open behind the app.
        if (owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return dialog.ShowDialog() == true;
    }

    private static (string Glyph, Brush Brush) Decoration(MessageDialogKind kind) => kind switch
    {
        MessageDialogKind.Question => ("?", new SolidColorBrush(Color.FromRgb(0x4C, 0xA0, 0xE8))),
        MessageDialogKind.Warning => ("!", new SolidColorBrush(Color.FromRgb(0xE0, 0x9B, 0x1A))),
        MessageDialogKind.Error => ("✕", new SolidColorBrush(Color.FromRgb(0xE8, 0x4B, 0x3C))),
        _ => ("i", new SolidColorBrush(Color.FromRgb(0x4C, 0xA0, 0xE8))),
    };

    private void OnPrimary(object sender, RoutedEventArgs e) => DialogResult = true;
}
