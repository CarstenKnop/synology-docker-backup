using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SshDockerBackup.App.Services;

namespace SshDockerBackup.App.ViewModels;

public sealed partial class LogViewModel : ObservableObject
{
    private const int MaxRows = 2000;

    private readonly IDialogService _dialogs;

    public LogViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;

        foreach (var entry in UiLogSink.Snapshot())
            Add(entry);

        UiLogSink.Emitted += OnEmitted;
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];

    [ObservableProperty] public partial bool ShowDebug { get; set; }
    [ObservableProperty] public partial bool AutoScroll { get; set; } = true;

    public string LogDirectory => App.LogDirectory;

    private void OnEmitted(LogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) Add(entry);
        else dispatcher.BeginInvoke(() => Add(entry));
    }

    private void Add(LogEntry entry)
    {
        // The file sink keeps everything; the UI only needs the interesting part by default.
        if (!ShowDebug && entry.Level is "DBG" or "VRB") return;

        Entries.Add(entry);

        while (Entries.Count > MaxRows)
            Entries.RemoveAt(0);
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    [RelayCommand]
    private void OpenLogFolder() => _dialogs.OpenInExplorer(App.LogDirectory);

    [RelayCommand]
    private void CopyAll()
    {
        if (Entries.Count == 0) return;

        var text = new StringBuilder();
        foreach (var entry in Entries)
            text.AppendLine(entry.Display);

        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (Exception ex)
        {
            // The clipboard is occasionally locked by another process; not worth crashing over.
            _dialogs.ShowError("Could not copy", ex.Message);
        }
    }
}
