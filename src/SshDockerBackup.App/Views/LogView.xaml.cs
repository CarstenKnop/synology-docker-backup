using System.Collections.Specialized;
using System.Windows.Controls;
using SshDockerBackup.App.ViewModels;

namespace SshDockerBackup.App.Views;

public partial class LogView : UserControl
{
    private INotifyCollectionChanged? _observed;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_observed is not null)
            _observed.CollectionChanged -= OnEntriesChanged;

        _observed = (DataContext as LogViewModel)?.Entries;

        if (_observed is not null)
            _observed.CollectionChanged += OnEntriesChanged;
    }

    /// <summary>Follows the tail while "Follow" is ticked, so a running backup stays readable.</summary>
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (DataContext is not LogViewModel { AutoScroll: true }) return;
        if (LogList.Items.Count == 0) return;

        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
