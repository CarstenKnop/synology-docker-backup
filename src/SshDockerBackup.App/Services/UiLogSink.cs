using Serilog.Core;
using Serilog.Events;

namespace SshDockerBackup.App.Services;

public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message, string? Exception)
{
    public string Display => Exception is null
        ? $"{Timestamp.LocalDateTime:HH:mm:ss} [{Level}] {Message}"
        : $"{Timestamp.LocalDateTime:HH:mm:ss} [{Level}] {Message}{Environment.NewLine}{Exception}";
}

/// <summary>
/// Mirrors Serilog output into the Log tab. Entries written before the view model subscribes are
/// buffered, so startup and connection errors are not lost.
/// </summary>
public sealed class UiLogSink : ILogEventSink
{
    private const int MaxBuffered = 500;

    private static readonly Lock Gate = new();
    private static readonly List<LogEntry> Buffer = [];

    public static event Action<LogEntry>? Emitted;

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(
            logEvent.Timestamp,
            Abbreviate(logEvent.Level),
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());

        lock (Gate)
        {
            if (Buffer.Count >= MaxBuffered) Buffer.RemoveAt(0);
            Buffer.Add(entry);
        }

        Emitted?.Invoke(entry);
    }

    /// <summary>Returns everything logged so far. Called once when the Log tab first binds.</summary>
    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Gate)
        {
            return [.. Buffer];
        }
    }

    private static string Abbreviate(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "???",
    };
}
