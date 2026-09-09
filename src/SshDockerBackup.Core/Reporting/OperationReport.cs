using System.Text;

namespace SshDockerBackup.Core.Reporting;

public enum ReportSeverity
{
    /// <summary>Neutral fact.</summary>
    Info,
    /// <summary>Something that went right, or is safely covered.</summary>
    Good,
    /// <summary>Not a failure, but worth knowing before or after the fact.</summary>
    Warning,
    /// <summary>Something the reader has to do themselves. No tool can do it for them.</summary>
    Action,
    /// <summary>Something actually went wrong.</summary>
    Problem,
}

public sealed record ReportLine(string Text, ReportSeverity Severity = ReportSeverity.Info);

public sealed class ReportSection(string title, string? intro = null)
{
    public string Title { get; } = title;

    /// <summary>One sentence explaining why this section exists, in plain terms.</summary>
    public string? Intro { get; } = intro;

    public List<ReportLine> Lines { get; } = [];

    public ReportSection Add(string text, ReportSeverity severity = ReportSeverity.Info)
    {
        Lines.Add(new ReportLine(text, severity));
        return this;
    }

    public bool HasContent => Lines.Count > 0;
}

/// <summary>
/// What happened, or what is about to. Written to be read by someone who does not want to learn how
/// Docker stores things: a short plain-language overview first, then the detail behind it.
/// </summary>
public sealed class OperationReport(string title, string subtitle)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    /// <summary>The overview: a handful of sentences that stand on their own.</summary>
    public List<ReportLine> Headlines { get; } = [];

    public List<ReportSection> Sections { get; } = [];

    public OperationReport Headline(string text, ReportSeverity severity = ReportSeverity.Info)
    {
        Headlines.Add(new ReportLine(text, severity));
        return this;
    }

    public ReportSection Section(string title, string? intro = null)
    {
        var section = new ReportSection(title, intro);
        Sections.Add(section);
        return section;
    }

    public bool HasProblems => Headlines.Concat(Sections.SelectMany(s => s.Lines))
        .Any(l => l.Severity == ReportSeverity.Problem);

    public bool NeedsAction => Headlines.Concat(Sections.SelectMany(s => s.Lines))
        .Any(l => l.Severity == ReportSeverity.Action);

    /// <summary>Plain text, for the saved copy. Readable in Notepad with no rendering.</summary>
    public string ToPlainText()
    {
        var sb = new StringBuilder();

        sb.AppendLine(Title);
        sb.AppendLine(new string('=', Title.Length));
        sb.AppendLine(Subtitle);
        sb.AppendLine($"Written {CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        if (Headlines.Count > 0)
        {
            sb.AppendLine("IN SHORT");
            sb.AppendLine("--------");
            foreach (var line in Headlines)
                sb.AppendLine($"  {Marker(line.Severity)} {line.Text}");
            sb.AppendLine();
        }

        foreach (var section in Sections.Where(s => s.HasContent))
        {
            sb.AppendLine(section.Title.ToUpperInvariant());
            sb.AppendLine(new string('-', section.Title.Length));

            if (!string.IsNullOrWhiteSpace(section.Intro))
            {
                sb.AppendLine(section.Intro);
                sb.AppendLine();
            }

            foreach (var line in section.Lines)
                sb.AppendLine($"  {Marker(line.Severity)} {line.Text}");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>ASCII markers rather than symbols, so the file looks the same in every editor.</summary>
    private static string Marker(ReportSeverity severity) => severity switch
    {
        ReportSeverity.Good => "[ok]  ",
        ReportSeverity.Warning => "[note]",
        ReportSeverity.Action => "[todo]",
        ReportSeverity.Problem => "[FAIL]",
        _ => "      ",
    };
}
