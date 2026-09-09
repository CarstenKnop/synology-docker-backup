using System.Text.Json.Serialization;

namespace SshDockerBackup.Core.Models;

/// <summary>Everything needed to reach the Docker host over SSH.</summary>
public sealed class ConnectionSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";

    /// <summary>Optional private key. When set, key auth is tried before password auth.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>Synology and most non-root setups need sudo to reach the docker socket.</summary>
    public bool UseSudo { get; set; } = true;

    public int ConnectTimeoutSeconds { get; set; } = 20;

    /// <summary>Long operations (tar of a large volume) must not be killed by the command timeout.</summary>
    public int CommandTimeoutMinutes { get; set; } = 240;

    /// <summary>SHA256 fingerprint accepted on a previous connection (trust on first use).</summary>
    public string? KnownHostFingerprint { get; set; }

    /// <summary>
    /// Never serialized. Held in memory only for the lifetime of the session, and used both for
    /// SSH auth and for feeding "sudo -S" over stdin.
    /// </summary>
    [JsonIgnore]
    public string? Password { get; set; }

    public ConnectionSettings Clone() => (ConnectionSettings)MemberwiseClone();

    public override string ToString() => $"{Username}@{Host}:{Port}";
}
