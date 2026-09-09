using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SshDockerBackup.Core.Reporting;

namespace SshDockerBackup.App.ViewModels;

public sealed partial class ReportViewModel(
    OperationReport report,
    bool isConfirmation,
    ILogger<ReportViewModel> log) : ObservableObject
{
    /// <summary>Where saved copies go when the report is not tied to a backup folder.</summary>
    public static string ReportDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SshDockerBackup", "reports");

    public OperationReport Report { get; } = report;

    /// <summary>True when the report is shown before an operation and offers a way out of it.</summary>
    public bool IsConfirmation { get; } = isConfirmation;

    public string Title => Report.Title;
    public string Subtitle => Report.Subtitle;
    public IReadOnlyList<ReportLine> Headlines => Report.Headlines;
    public IReadOnlyList<ReportSection> Sections => [.. Report.Sections.Where(s => s.HasContent)];

    public string ContinueText => IsConfirmation ? "Continue" : "Close";
    public bool ShowCancel => IsConfirmation;

    [ObservableProperty] public partial string SavedMessage { get; set; } = "";

    /// <summary>Set when the caller has already written a copy somewhere meaningful.</summary>
    public string? PreSavedPath { get; init; }

    [RelayCommand]
    private void SaveReport()
    {
        try
        {
            Directory.CreateDirectory(ReportDirectory);

            var stamp = Report.CreatedAt.ToString("yyyyMMdd-HHmmss");
            var name = new string(Report.Title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            var path = Path.Combine(ReportDirectory, $"{name}-{stamp}.txt");

            File.WriteAllText(path, Report.ToPlainText());
            SavedMessage = $"Saved to {path}";
            log.LogInformation("Report saved to {Path}", path);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not save the report");
            SavedMessage = $"Could not save the report: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenSavedCopy()
    {
        if (PreSavedPath is not { Length: > 0 } path || !File.Exists(path)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not open {Path}", path);
            SavedMessage = ex.Message;
        }
    }

    public bool HasPreSavedCopy => PreSavedPath is { Length: > 0 };
}
