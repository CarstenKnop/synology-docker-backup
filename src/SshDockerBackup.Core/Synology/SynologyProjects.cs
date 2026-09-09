using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SshDockerBackup.Core.Ssh;

namespace SshDockerBackup.Core.Synology;

/// <summary>
/// Container Manager's Project list is Synology's own registry, kept separately from Docker. A
/// container can be restored perfectly — right name, labels, networks, data — and still not appear
/// under its project, because that entry lives here and nothing at the Docker level writes it.
///
/// DSM exposes it through synowebapi, which is undocumented but is the mechanism Container Manager
/// itself uses. Creating an entry was verified not to touch the project's compose file.
/// </summary>
public sealed record SynologyProject(string Id, string Name, string Path, string Status);

public interface ISynologyProjects
{
    /// <summary>False on anything that is not DSM, so the rest can be skipped quietly.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Registered projects, keyed by name.</summary>
    Task<IReadOnlyDictionary<string, SynologyProject>> ListAsync(CancellationToken ct = default);

    /// <summary>Registers a project against an existing folder. Adopts containers already running.</summary>
    Task<bool> CreateAsync(string name, string path, CancellationToken ct = default);

    /// <summary>
    /// Moves a freshly created project out of CREATED, which is what leaves the Project row grey
    /// and its Start action disabled. Build is the transition DSM itself uses; for a compose file
    /// with no build: section it amounts to a compose up.
    /// </summary>
    Task<bool> BuildAsync(string id, CancellationToken ct = default);
}

public sealed partial class SynologyProjects(ISshSession ssh, ILogger<SynologyProjects> log) : ISynologyProjects
{
    private const string WebApi = "/usr/syno/bin/synowebapi";
    private const string Api = "api=SYNO.Docker.Project version=1";

    private bool? _available;

    private bool Elevate => ssh.Settings?.UseSudo ?? true;

    [GeneratedRegex(@"^/volume[^/]*", RegexOptions.IgnoreCase)]
    private static partial Regex VolumePrefix();

    /// <summary>
    /// DSM stores the share-relative form alongside the absolute one: /volume1/docker/app is
    /// /docker/app. Derived rather than guessed at, matching what an existing entry looks like.
    /// </summary>
    public static string ToSharePath(string absolutePath)
    {
        var stripped = VolumePrefix().Replace(absolutePath.TrimEnd('/'), "");
        return stripped.Length == 0 ? absolutePath : stripped;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available is { } known) return known;

        var result = await ssh.RunAsync($"[ -x {SshSession.Quote(WebApi)} ] && echo yes || echo no", Elevate, ct)
            .ConfigureAwait(false);

        _available = result.StdOut.Trim() == "yes";
        log.LogInformation("Synology project API {State}", _available.Value ? "available" : "not present");
        return _available.Value;
    }

    public async Task<IReadOnlyDictionary<string, SynologyProject>> ListAsync(CancellationToken ct = default)
    {
        var projects = new Dictionary<string, SynologyProject>(StringComparer.Ordinal);

        var result = await ssh.RunAsync($"{WebApi} --exec {Api} method=list 2>&1", Elevate, ct).ConfigureAwait(false);

        if (!TryExtractJson(result.StdOut, out var json))
        {
            log.LogWarning("Could not read the Synology project list: {Output}", Truncate(result.StdOut));
            return projects;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return projects;

            // The object is keyed by project id; name, path and status live inside each entry.
            foreach (var entry in data.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;

                var name = entry.Value.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var path = entry.Value.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                var status = entry.Value.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";

                projects[name] = new SynologyProject(entry.Name, name, path, status);
            }
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Could not parse the Synology project list");
        }

        return projects;
    }

    public async Task<bool> BuildAsync(string id, CancellationToken ct = default)
    {
        log.LogInformation("Building Synology project {Id} so DSM records its own status", id);

        var result = await ssh.RunAsync(
            $"{WebApi} --exec {Api} method=build id={SshSession.Quote(id)} 2>&1", Elevate, ct).ConfigureAwait(false);

        return ReadSuccess(result.StdOut, $"build project {id}");
    }

    public async Task<bool> CreateAsync(string name, string path, CancellationToken ct = default)
    {
        var sharePath = ToSharePath(path);

        var command =
            $"{WebApi} --exec {Api} method=create " +
            $"name={SshSession.Quote(name)} " +
            $"path={SshSession.Quote(path)} " +
            $"share_path={SshSession.Quote(sharePath)} 2>&1";

        log.LogInformation("Registering Synology project {Name} at {Path} (share {Share})", name, path, sharePath);

        var result = await ssh.RunAsync(command, Elevate, ct).ConfigureAwait(false);
        return ReadSuccess(result.StdOut, $"register project {name}");
    }

    /// <summary>Reads synowebapi's success flag, logging DSM's error code when it refuses.</summary>
    private bool ReadSuccess(string output, string what)
    {
        if (TryExtractJson(output, out var json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("success", out var success) &&
                    success.ValueKind == JsonValueKind.True)
                {
                    log.LogInformation("Synology API: {What} succeeded", what);
                    return true;
                }

                var code = doc.RootElement.TryGetProperty("error", out var error) &&
                           error.TryGetProperty("code", out var c)
                    ? c.ToString()
                    : "unknown";

                log.LogWarning("Synology API refused to {What}: error {Code}", what, code);
                return false;
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Could not parse the Synology response while trying to {What}", what);
            }
        }

        log.LogWarning("Unexpected Synology response while trying to {What}: {Output}", what, Truncate(output));
        return false;
    }

    /// <summary>
    /// synowebapi prints diagnostics such as "[Line 295] Exec WebAPI: ..." before the JSON body,
    /// so the payload has to be carved out rather than parsed from the first character.
    /// </summary>
    private static bool TryExtractJson(string output, out string json)
    {
        json = "";
        if (string.IsNullOrWhiteSpace(output)) return false;

        // synowebapi prints its own diagnostics before the response, and those lines carry braces:
        // "[Line 295] Exec WebAPI: ... param={...}, runner=SYSTEM_ADMIN" in particular. Taking the
        // first '{' in the whole output therefore grabs the echoed parameters instead of the result,
        // and parsing stops at the comma that follows the closing brace. Every diagnostic line
        // starts with "[Line N]", so the response is whatever comes after them.
        var lines = output.Split('\n');
        var bodyStart = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("[Line ", StringComparison.Ordinal)) continue;
            if (!trimmed.StartsWith('{')) continue;

            bodyStart = i;
            break;
        }

        // No recognisable body: fall back to the whole output, so a response in some other shape
        // still gets a chance rather than being discarded here.
        var body = bodyStart < 0 ? output : string.Join('\n', lines[bodyStart..]);

        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        if (start < 0 || end <= start) return false;

        json = body[start..(end + 1)];
        return true;
    }

    private static string Truncate(string value) =>
        value.Length <= 400 ? value.Trim() : value[..400].Trim() + "...";
}
