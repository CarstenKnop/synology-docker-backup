using System.Text.Json;
using Microsoft.Extensions.Logging;
using SshDockerBackup.Core.Models;
using SshDockerBackup.Core.Ssh;

namespace SshDockerBackup.Core.Docker;

/// <summary>
/// Drives the docker CLI on the remote host. Everything goes through the SSH session, so there is
/// no Docker API socket to expose and nothing to install on the NAS.
/// </summary>
public sealed class DockerService(ISshSession ssh, ILogger<DockerService> log) : IDockerService
{
    private bool Elevate => ssh.Settings?.UseSudo ?? true;

    /// <summary>
    /// Extra tar flags this host's tar actually understands, worked out once during ProbeAsync.
    /// Synology stores its ACLs in extended attributes, and plain tar silently drops them, so a
    /// restored folder would come back with the right owner but the wrong share permissions.
    /// </summary>
    private string _tarPreserveFlags = "";

    private async Task DetectTarCapabilitiesAsync(CancellationToken ct)
    {
        var probe = await ssh.RunAsync(
            "tar --help 2>&1 | grep -c -- --xattrs; tar --help 2>&1 | grep -c -- --acls",
            Elevate, ct).ConfigureAwait(false);

        var lines = probe.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var hasXattrs = lines.Length > 0 && int.TryParse(lines[0].Trim(), out var x) && x > 0;
        var hasAcls = lines.Length > 1 && int.TryParse(lines[1].Trim(), out var a) && a > 0;

        var flags = new List<string>();
        if (hasXattrs)
        {
            // GNU tar stores only user.* xattrs unless told otherwise, and Synology ACLs live in
            // the system.* and security.* namespaces.
            flags.Add("--xattrs");
            flags.Add("--xattrs-include='*'");
        }
        if (hasAcls) flags.Add("--acls");

        _tarPreserveFlags = flags.Count == 0 ? "" : " " + string.Join(' ', flags);

        log.LogInformation("tar capabilities: xattrs={Xattrs}, acls={Acls}. Extra flags:{Flags}",
            hasXattrs, hasAcls, string.IsNullOrEmpty(_tarPreserveFlags) ? " (none)" : _tarPreserveFlags);

        if (!hasXattrs)
        {
            log.LogWarning(
                "This host's tar cannot store extended attributes, so Synology ACLs will not survive " +
                "a restore. File ownership and permission bits still will.");
        }
    }

    public async Task<DockerHostInfo> ProbeAsync(CancellationToken ct = default)
    {
        // Fail early and clearly if sudo is misconfigured, rather than midway through a backup.
        var whoami = await ssh.RunAsync("id -un", Elevate, ct).ConfigureAwait(false);
        if (!whoami.Success)
        {
            throw new SshCommandException(
                "Could not run a command with sudo. Check the password, and that the account is in " +
                $"the administrators group. Remote error: {whoami.StdErr}");
        }
        log.LogInformation("Elevated commands run as {User}", whoami.StdOut.Trim());

        var version = await ssh.RunAsync("docker version --format '{{.Server.Version}}'", Elevate, ct)
            .ConfigureAwait(false);
        if (!version.Success)
        {
            throw new SshCommandException(
                "docker is reachable over SSH but did not respond. Is Container Manager running? " +
                $"Remote error: {version.StdErr}");
        }

        var root = await ssh.RunAsync("docker info --format '{{.DockerRootDir}}'", Elevate, ct)
            .ConfigureAwait(false);
        var host = await ssh.RunAsync("hostname", Elevate, ct).ConfigureAwait(false);

        await DetectTarCapabilitiesAsync(ct).ConfigureAwait(false);

        var info = new DockerHostInfo(
            version.StdOut.Trim(),
            root.Success ? root.StdOut.Trim() : "",
            host.Success ? host.StdOut.Trim() : ssh.Settings?.Host ?? "");

        log.LogInformation("Docker {Version} on {Host}, root dir {Root}", info.Version, info.HostName, info.RootDir);
        return info;
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(CancellationToken ct = default)
    {
        var list = await ssh.RunAsync("docker ps -a --format '{{json .}}'", Elevate, ct).ConfigureAwait(false);
        list.EnsureSuccess("docker ps");

        var summaries = new List<ContainerInfo>();
        foreach (var line in list.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                summaries.Add(new ContainerInfo
                {
                    Id = Str(root, "ID"),
                    Name = Str(root, "Names"),
                    Image = Str(root, "Image"),
                    State = Str(root, "State"),
                    Status = Str(root, "Status"),
                    Ports = Str(root, "Ports"),
                });
            }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Skipping unparseable docker ps line: {Line}", trimmed);
            }
        }

        if (summaries.Count == 0)
        {
            log.LogInformation("No containers found on the host");
            return summaries;
        }

        // One inspect call for every container beats N round trips over SSH.
        var ids = string.Join(' ', summaries.Select(c => c.Id));
        var inspect = await ssh.RunAsync($"docker inspect {ids}", Elevate, ct).ConfigureAwait(false);

        if (!inspect.Success)
        {
            log.LogWarning("docker inspect failed, mount details unavailable: {Error}", inspect.StdErr);
            return summaries;
        }

        try
        {
            using var doc = JsonDocument.Parse(inspect.StdOut);

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var fullId = Str(element, "Id");
                var match = summaries.FirstOrDefault(c => fullId.StartsWith(c.Id, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;

                match.InspectJson = element.GetRawText();
                match.Mounts = ParseMounts(element);
                match.ComposeProject = ReadLabel(element, "com.docker.compose.project");

                // config_files may list several; the first is the project's primary definition.
                match.ComposeConfigFile = ReadLabel(element, "com.docker.compose.project.config_files")
                    ?.Split(',')
                    .Select(f => f.Trim())
                    .FirstOrDefault(f => f.Length > 0);
            }
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Could not parse docker inspect output");
        }

        return summaries;
    }

    private static IReadOnlyList<MountInfo> ParseMounts(JsonElement container)
    {
        if (!container.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<MountInfo>();
        foreach (var mount in mounts.EnumerateArray())
        {
            var type = Str(mount, "Type");
            result.Add(new MountInfo
            {
                Kind = type.ToLowerInvariant() switch
                {
                    "volume" => MountKind.Volume,
                    "bind" => MountKind.Bind,
                    "tmpfs" => MountKind.Tmpfs,
                    _ => MountKind.Other,
                },
                Name = Str(mount, "Name"),
                Source = Str(mount, "Source"),
                Destination = Str(mount, "Destination"),
                ReadWrite = mount.TryGetProperty("RW", out var rw) && rw.ValueKind == JsonValueKind.True,
            });
        }
        return result;
    }

    private static string? ReadLabel(JsonElement container, string label)
    {
        if (!container.TryGetProperty("Config", out var config)) return null;
        if (!config.TryGetProperty("Labels", out var labels) || labels.ValueKind != JsonValueKind.Object) return null;

        return labels.TryGetProperty(label, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    public async Task<string> InspectAsync(string containerId, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync($"docker inspect {SshSession.Quote(containerId)}", Elevate, ct)
            .ConfigureAwait(false);
        result.EnsureSuccess($"docker inspect {containerId}");
        return result.StdOut;
    }

    public async Task<string?> GetVolumeMountpointAsync(string volumeName, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(
            $"docker volume inspect --format '{{{{.Mountpoint}}}}' {SshSession.Quote(volumeName)}",
            Elevate, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            log.LogWarning("Could not resolve mountpoint for volume {Volume}: {Error}", volumeName, result.StdErr);
            return null;
        }

        var path = result.StdOut.Trim();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public async Task<long> GetPathSizeBytesAsync(string path, CancellationToken ct = default)
    {
        // -sk rather than -sb: BusyBox du on some DSM builds has no -b.
        var result = await ssh.RunAsync($"du -sk {SshSession.Quote(path)} 2>/dev/null | cut -f1", Elevate, ct)
            .ConfigureAwait(false);

        var text = result.StdOut.Trim();
        return long.TryParse(text, out var kb) ? kb * 1024 : 0;
    }

    public async Task<string?> GetImageIdAsync(string imageTag, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(
            $"docker image inspect --format '{{{{.Id}}}}' {SshSession.Quote(imageTag)}",
            Elevate, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            log.LogWarning("Could not read the image ID of {Image}: {Error}", imageTag, result.StdErr);
            return null;
        }

        var id = result.StdOut.Trim();
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// An image that was pulled from, or pushed to, a registry has at least one RepoDigest
    /// (repo@sha256:...). One built locally with `docker build` has none, which makes an empty
    /// RepoDigests list the practical test for "nothing can pull this back for me".
    /// </summary>
    public async Task<bool?> IsImagePullableAsync(string imageTag, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(
            $"docker image inspect --format '{{{{len .RepoDigests}}}}' {SshSession.Quote(imageTag)}",
            Elevate, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            log.LogWarning("Could not inspect image {Image}: {Error}", imageTag, result.StdErr);
            return null;
        }

        if (!int.TryParse(result.StdOut.Trim(), out var digestCount))
        {
            log.LogWarning("Unexpected RepoDigests output for {Image}: {Output}", imageTag, result.StdOut.Trim());
            return null;
        }

        var pullable = digestCount > 0;
        log.LogDebug("Image {Image} has {Count} repo digest(s): {Verdict}",
            imageTag, digestCount, pullable ? "pullable" : "local only");

        return pullable;
    }

    public async Task StopContainerAsync(string idOrName, CancellationToken ct = default)
    {
        log.LogInformation("Stopping container {Container}", idOrName);
        var result = await ssh.RunAsync($"docker stop {SshSession.Quote(idOrName)}", Elevate, ct).ConfigureAwait(false);
        result.EnsureSuccess($"docker stop {idOrName}");
    }

    public async Task StartContainerAsync(string idOrName, CancellationToken ct = default)
    {
        log.LogInformation("Starting container {Container}", idOrName);
        var result = await ssh.RunAsync($"docker start {SshSession.Quote(idOrName)}", Elevate, ct).ConfigureAwait(false);
        if (!result.Success)
            log.LogError("Could not restart {Container}: {Error}", idOrName, result.StdErr);
    }

    public async Task<bool> PathExistsAsync(string path, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(
            $"[ -e {SshSession.Quote(path)} ] && echo yes || echo no", Elevate, ct).ConfigureAwait(false);
        return result.StdOut.Trim() == "yes";
    }

    public async Task<IReadOnlyList<string>> FindProjectFoldersAsync(
        IEnumerable<string> bindPaths, CancellationToken ct = default)
    {
        var paths = bindPaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.StartsWith('/'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (paths.Count == 0) return [];

        var quoted = string.Join(' ', paths.Select(SshSession.Quote));

        // Climb at most four levels so a deep mount cannot walk all the way to /.
        var script = $$"""
            for p in {{quoted}}; do
              d="$p"
              n=0
              while [ "$n" -lt 4 ] && [ -n "$d" ] && [ "$d" != "/" ] && [ "$d" != "." ]; do
                for f in docker-compose.yml docker-compose.yaml compose.yml compose.yaml Dockerfile; do
                  if [ -f "$d/$f" ]; then echo "$d"; break; fi
                done
                d=$(dirname "$d")
                n=$((n+1))
              done
            done | sort -u
            """;

        var result = await ssh.RunAsync(script, Elevate, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            log.LogWarning("Project folder scan failed: {Error}", result.StdErr);
            return [];
        }

        var found = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith('/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        log.LogInformation("Project folder scan found {Count}: {Folders}", found.Count, string.Join(", ", found));
        return found;
    }

    public async Task<NetworkDefinition?> InspectNetworkAsync(string name, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync($"docker network inspect {SshSession.Quote(name)}", Elevate, ct)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            log.LogWarning("Could not inspect network {Network}: {Error}", name, result.StdErr);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var network = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().FirstOrDefault()
                : doc.RootElement;

            if (network.ValueKind != JsonValueKind.Object) return null;

            var definition = new NetworkDefinition
            {
                Name = Str(network, "Name"),
                Driver = Str(network, "Driver") is { Length: > 0 } driver ? driver : "bridge",
                Internal = IsTrue(network, "Internal"),
                Attachable = IsTrue(network, "Attachable"),
                EnableIPv6 = IsTrue(network, "EnableIPv6"),
            };

            if (network.TryGetProperty("IPAM", out var ipam) &&
                ipam.TryGetProperty("Config", out var configs) &&
                configs.ValueKind == JsonValueKind.Array)
            {
                foreach (var config in configs.EnumerateArray())
                {
                    var subnet = Str(config, "Subnet");
                    if (subnet.Length == 0) continue;

                    definition.Subnets.Add(new NetworkSubnet
                    {
                        Subnet = subnet,
                        Gateway = Str(config, "Gateway") is { Length: > 0 } gw ? gw : null,
                        IpRange = Str(config, "IPRange") is { Length: > 0 } range ? range : null,
                    });
                }
            }

            CopyStringMap(network, "Options", definition.Options);
            CopyStringMap(network, "Labels", definition.Labels);

            log.LogInformation("Network {Network}: driver {Driver}, subnets {Subnets}",
                definition.Name, definition.Driver,
                definition.Subnets.Count == 0 ? "(default)" : string.Join(", ", definition.Subnets.Select(s => s.Subnet)));

            return definition;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Could not parse the definition of network {Network}", name);
            return null;
        }
    }

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static void CopyStringMap(JsonElement parent, string name, Dictionary<string, string> target)
    {
        if (!parent.TryGetProperty(name, out var map) || map.ValueKind != JsonValueKind.Object) return;

        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
                target[entry.Name] = entry.Value.GetString() ?? "";
        }
    }

    public async Task<IReadOnlyList<string>> FindMissingImagesAsync(
        IEnumerable<string> images, CancellationToken ct = default)
    {
        var list = images
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (list.Count == 0) return [];

        var quoted = string.Join(' ', list.Select(SshSession.Quote));

        // One round trip: print only the ones docker cannot already resolve locally.
        var script = $$"""
            for i in {{quoted}}; do
              docker image inspect "$i" >/dev/null 2>&1 || printf '%s\n' "$i"
            done
            """;

        var result = await ssh.RunAsync(script, Elevate, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            log.LogWarning("Could not check which images are present: {Error}", result.StdErr);
            return [];
        }

        var missing = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        log.LogInformation("{Missing} of {Total} images are not on the host", missing.Count, list.Count);
        return missing;
    }

    public async Task<bool?> CanReachRegistryAsync(CancellationToken ct = default)
    {
        // Docker Hub answers /v2/ with 401 when unauthenticated, which still proves reachability.
        // curl reports 000 when it could not connect at all, and ping is the fallback for hosts
        // without curl. An inconclusive result is reported as null rather than as "offline".
        const string script = """
            if command -v curl >/dev/null 2>&1; then
              code=$(curl -s -o /dev/null -m 8 -w '%{http_code}' https://registry-1.docker.io/v2/ 2>/dev/null)
              if [ -n "$code" ] && [ "$code" != "000" ]; then echo online; else echo offline; fi
            elif command -v wget >/dev/null 2>&1; then
              if wget -q --timeout=8 --spider https://registry-1.docker.io/v2/ 2>/dev/null; then echo online; else echo offline; fi
            elif ping -c 1 -W 3 1.1.1.1 >/dev/null 2>&1; then
              echo online
            else
              echo unknown
            fi
            """;

        var result = await ssh.RunAsync(script, Elevate, ct).ConfigureAwait(false);
        var answer = result.StdOut.Trim();

        log.LogInformation("Registry reachability from the host: {Answer}", answer);

        return answer switch
        {
            "online" => true,
            "offline" => false,
            _ => null,
        };
    }

    public async Task<bool> VolumeExistsAsync(string volumeName, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync(
            $"docker volume inspect {SshSession.Quote(volumeName)} >/dev/null 2>&1 && echo yes || echo no",
            Elevate, ct).ConfigureAwait(false);
        return result.StdOut.Trim() == "yes";
    }

    public async Task CreateVolumeAsync(string volumeName, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync($"docker volume create {SshSession.Quote(volumeName)}", Elevate, ct)
            .ConfigureAwait(false);
        result.EnsureSuccess($"docker volume create {volumeName}");
    }

    public async Task EnsureDirectoryAsync(string path, CancellationToken ct = default)
    {
        var result = await ssh.RunAsync($"mkdir -p {SshSession.Quote(path)}", Elevate, ct).ConfigureAwait(false);
        result.EnsureSuccess($"mkdir -p {path}");
    }

    public async Task<RemotePathKind> InspectPathAsync(string path, CancellationToken ct = default)
    {
        // -f is true for a regular file or a symlink to one; a socket or device falls through to
        // the -e branch, and a broken symlink is caught by -L.
        var script =
            $"p={SshSession.Quote(path)}; " +
            "if [ -d \"$p\" ]; then echo dir; " +
            "elif [ -f \"$p\" ]; then echo file; " +
            "elif [ -e \"$p\" ] || [ -L \"$p\" ]; then echo special; " +
            "else echo missing; fi";

        var result = await ssh.RunAsync(script, Elevate, ct).ConfigureAwait(false);

        return result.StdOut.Trim() switch
        {
            "dir" => RemotePathKind.Directory,
            "file" => RemotePathKind.File,
            "special" => RemotePathKind.Special,
            _ => RemotePathKind.Missing,
        };
    }

    public async Task<IReadOnlyDictionary<string, RemotePathKind>> InspectPathsAsync(
        IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var results = new Dictionary<string, RemotePathKind>(StringComparer.Ordinal);
        if (list.Count == 0) return results;

        var quoted = string.Join(' ', list.Select(SshSession.Quote));

        // Kind first, then the path, so a path containing whitespace still parses on the first tab.
        var script = $$"""
            for p in {{quoted}}; do
              if [ -d "$p" ]; then k=dir
              elif [ -f "$p" ]; then k=file
              elif [ -e "$p" ] || [ -L "$p" ]; then k=special
              else k=missing; fi
              printf '%s\t%s\n' "$k" "$p"
            done
            """;

        var result = await ssh.RunAsync(script, Elevate, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            log.LogWarning("Bulk path check failed: {Error}", result.StdErr);
            return results;
        }

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            var kind = line[..tab].Trim();
            var path = line[(tab + 1)..].TrimEnd('\r');

            results[path] = kind switch
            {
                "dir" => RemotePathKind.Directory,
                "file" => RemotePathKind.File,
                "special" => RemotePathKind.Special,
                _ => RemotePathKind.Missing,
            };
        }

        return results;
    }

    public async Task<IReadOnlyDictionary<string, long>> GetPathSizesAsync(
        IEnumerable<string> paths, CancellationToken ct = default)
    {
        var list = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        if (list.Count == 0) return sizes;

        var quoted = string.Join(' ', list.Select(SshSession.Quote));

        // -sk rather than -sb: BusyBox du on some DSM builds has no -b.
        var script = $$"""
            for p in {{quoted}}; do
              s=$(du -sk "$p" 2>/dev/null | cut -f1)
              [ -z "$s" ] && s=0
              printf '%s\t%s\n' "$s" "$p"
            done
            """;

        var result = await ssh.RunAsync(script, Elevate, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            log.LogWarning("Bulk size check failed: {Error}", result.StdErr);
            return sizes;
        }

        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;

            if (long.TryParse(line[..tab].Trim(), out var kb))
                sizes[line[(tab + 1)..].TrimEnd('\r')] = kb * 1024;
        }

        return sizes;
    }

    // --numeric-owner keeps the raw uid/gid pairs. Container users rarely exist in the NAS passwd
    // file, so resolving names would rewrite ownership and break the app on restore.
    public string BuildArchiveCommand(string hostPath, bool isDirectory = true)
    {
        if (isDirectory)
            return $"tar --numeric-owner{_tarPreserveFlags} -czf - -C {SshSession.Quote(hostPath)} .";

        // tar cannot chdir into a file, so archive it by name from its parent directory.
        var trimmed = hostPath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        var parent = slash <= 0 ? "/" : trimmed[..slash];
        var name = trimmed[(slash + 1)..];

        return $"tar --numeric-owner{_tarPreserveFlags} -czf - -C {SshSession.Quote(parent)} {SshSession.Quote(name)}";
    }

    public string BuildExtractCommand(string hostPath) =>
        $"mkdir -p {SshSession.Quote(hostPath)} && " +
        $"tar --numeric-owner{_tarPreserveFlags} -xzf - -C {SshSession.Quote(hostPath)}";

    public string BuildExtractFromFileCommand(string archivePath, string hostPath) =>
        $"mkdir -p {SshSession.Quote(hostPath)} && " +
        $"tar --numeric-owner{_tarPreserveFlags} -xzf {SshSession.Quote(archivePath)} " +
        $"-C {SshSession.Quote(hostPath)}";

    public string BuildImageSaveCommand(string imageTag) =>
        $"docker save {SshSession.Quote(imageTag)} | gzip -c";

    public string BuildImageLoadCommand() => "gunzip -c | docker load";

    public string BuildImageLoadFromFileCommand(string archivePath) =>
        $"gunzip -c {SshSession.Quote(archivePath)} | docker load";

    public async Task<CommandResult> RunDockerAsync(string arguments, CancellationToken ct = default) =>
        await ssh.RunAsync($"docker {arguments}", Elevate, ct).ConfigureAwait(false);
}
