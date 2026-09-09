using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SshDockerBackup.Core.Models;

namespace SshDockerBackup.App.Services;

/// <summary>
/// Persisted preferences. The password is deliberately absent: ConnectionSettings.Password carries
/// [JsonIgnore], so it lives in memory for the session only and is never written to disk.
/// </summary>
public sealed class AppSettings
{
    public ConnectionSettings Connection { get; set; } = new();

    public string? LastBackupDestination { get; set; }
    public string? LastRestoreFolder { get; set; }

    /// <summary>Backup destination is a folder on the NAS (e.g. a USB drive) rather than this PC.</summary>
    public bool BackupDestinationOnHost { get; set; }

    /// <summary>Remembered separately from the local one, so switching modes does not lose either.</summary>
    public string? LastHostBackupDestination { get; set; }

    public bool RestoreSourceOnHost { get; set; }
    public string? LastHostRestoreFolder { get; set; }

    public bool StopContainersDuringBackup { get; set; } = true;
    public bool ComputeChecksums { get; set; } = true;

    /// <summary>Written as a name rather than a number so the file stays readable.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ImageBackupMode>))]
    public ImageBackupMode ImageMode { get; set; } = ImageBackupMode.All;

    /// <summary>Host folders to archive alongside the containers, such as project directories.</summary>
    public List<string> ExtraPaths { get; set; } = [];

    /// <summary>Host folders to leave out, even when a container mounts them.</summary>
    public List<string> ExcludedPaths { get; set; } = [];

    // Restore options. Every default here is the one that produces a complete restore, so a user
    // who never opens these gets the right behaviour.
    public bool RestoreVolumeData { get; set; } = true;
    public bool RestoreHostFolders { get; set; } = true;
    public bool RecreateContainers { get; set; } = true;
    public bool RestoreLoadImages { get; set; } = true;
    public bool VerifyChecksums { get; set; } = true;
    public bool PreserveRunningState { get; set; } = true;
    public bool RegisterSynologyProjects { get; set; } = true;

    // "Replace existing containers" is deliberately absent. It force-removes whatever is running
    // under a matching name, and a destructive option that quietly persists from a previous session
    // is exactly the kind of thing that ruins someone's evening. It starts off, every time.
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
    string FilePath { get; }
}

public sealed class SettingsStore(ILogger<SettingsStore> log) : ISettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Lock _gate = new();

    /// <summary>
    /// Every view model shares one instance. Handing out separate copies would mean the last one to
    /// Save wins and silently discards the others' settings.
    /// </summary>
    private AppSettings? _current;

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SshDockerBackup", "settings.json");

    public AppSettings Load()
    {
        lock (_gate)
        {
            return _current ??= ReadFromDisk();
        }
    }

    private AppSettings ReadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                log.LogInformation("No saved settings at {Path}, starting with defaults", FilePath);
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // Corrupt settings must not stop the app from starting.
            log.LogWarning(ex, "Could not read settings from {Path}, using defaults", FilePath);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            _current = settings;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
            log.LogDebug("Settings saved to {Path}", FilePath);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not save settings to {Path}", FilePath);
        }
    }
}
