namespace SshDockerBackup.Core.Backup;

/// <summary>A single progress tick. Reported often, so it stays cheap to construct.</summary>
public sealed record BackupProgress(
    string Stage,
    string Item,
    long BytesDone = 0,
    long? BytesTotal = null,
    int ItemIndex = 0,
    int ItemCount = 0)
{
    /// <summary>Overall completion across items, or null when the total is not yet known.</summary>
    public double? OverallFraction => ItemCount > 0
        ? Math.Clamp((double)ItemIndex / ItemCount, 0, 1)
        : null;

    /// <summary>Completion of the current item, or null for an unsized transfer.</summary>
    public double? ItemFraction => BytesTotal is > 0
        ? Math.Clamp((double)BytesDone / BytesTotal.Value, 0, 1)
        : null;

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }
}
