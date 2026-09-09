using SshDockerBackup.Core.Backup;
using SshDockerBackup.Core.Models;
using Xunit;

namespace SshDockerBackup.Core.Tests;

/// <summary>
/// One image can be named two ways. A container asking for <c>postgres</c> and another asking for
/// <c>postgres:latest</c> want the same bytes, and treating the text as the identity stored the
/// image twice — 565 MB of a 3.46 GB backup, on a real host. The same comparison decides whether
/// the restore pre-flight tells you an image needs downloading.
/// </summary>
public class ImageReferenceTests
{
    [Theory]
    [InlineData("postgres", "postgres:latest")]
    [InlineData("homeassistant/home-assistant", "homeassistant/home-assistant:latest")]
    [InlineData("ghcr.io/owner/app", "ghcr.io/owner/app:latest")]
    public void AnUntaggedReferenceGetsTheTagDockerWouldApply(string input, string expected) =>
        Assert.Equal(expected, RestoreService.NormaliseImageRef(input));

    [Theory]
    [InlineData("postgres:16.4")]
    [InlineData("mcr.microsoft.com/dotnet/aspnet:10.0")]
    [InlineData("openspeedtest/latest:latest")]
    public void AnExplicitTagIsLeftAlone(string reference) =>
        Assert.Equal(reference, RestoreService.NormaliseImageRef(reference));

    /// <summary>
    /// The case that makes this more than a string append: a colon in the registry part is a port,
    /// not a tag. Splitting on the first colon would turn "myreg:5000/app" into an image called
    /// "myreg" at tag "5000/app".
    /// </summary>
    [Fact]
    public void ARegistryPortIsNotMistakenForATag()
    {
        Assert.Equal("myreg:5000/app:latest", RestoreService.NormaliseImageRef("myreg:5000/app"));
        Assert.Equal("myreg:5000/app:2.1", RestoreService.NormaliseImageRef("myreg:5000/app:2.1"));
    }

    [Fact]
    public void ADigestReferenceIsAlreadyExact()
    {
        const string pinned = "postgres@sha256:9f3ac1e2b4d5a6f7c8b9e0d1a2f3c4b5a6d7e8f9c0b1a2d3e4f5c6b7a8d9e0f1";

        Assert.Equal(pinned, RestoreService.NormaliseImageRef(pinned));
    }

    [Fact]
    public void TwoSpellingsOfOneImageCompareEqual()
    {
        // This is the property the deduplication and the pre-flight both rely on.
        Assert.Equal(
            RestoreService.NormaliseImageRef("homeassistant/home-assistant"),
            RestoreService.NormaliseImageRef("homeassistant/home-assistant:latest"));
    }
}

/// <summary>
/// A stack deployed by something other than Container Manager records the path its own container
/// saw, not a path on the host — Portainer keeps stacks at <c>/data/compose/&lt;id&gt;</c>. When
/// that container also bind-mounts the folder the files really live in, the recorded path can be
/// translated back to a host path by walking the mounts the backup already captured.
/// </summary>
public class BindMountTranslationTests
{
    private static BackupManifest ManifestWithMount(string containerPath, string hostPath) => new()
    {
        Containers =
        [
            new ContainerBackup
            {
                Name = "portainer",
                Archives =
                [
                    new ArchiveEntry
                    {
                        Kind = ArchiveKind.Bind,
                        Source = hostPath,
                        Destination = containerPath,
                    },
                ],
            },
        ],
    };

    [Fact]
    public void TranslatesAContainerPathBackToItsHostPath()
    {
        var manifest = ManifestWithMount("/data", "/volume1/docker/portainer/data");

        Assert.Equal(
            "/volume1/docker/portainer/data/compose/1",
            RestoreService.TranslateThroughBindMounts("/data/compose/1", manifest));
    }

    [Fact]
    public void PrefersTheMostSpecificMount()
    {
        // Two mounts could both explain the path; the longer destination is the right answer,
        // otherwise a broad mount shadows the specific one that was actually meant.
        var manifest = new BackupManifest
        {
            Containers =
            [
                new ContainerBackup
                {
                    Name = "app",
                    Archives =
                    [
                        new ArchiveEntry { Kind = ArchiveKind.Bind, Source = "/volume1/broad", Destination = "/data" },
                        new ArchiveEntry { Kind = ArchiveKind.Bind, Source = "/volume1/exact", Destination = "/data/compose" },
                    ],
                },
            ],
        };

        Assert.Equal("/volume1/exact/1", RestoreService.TranslateThroughBindMounts("/data/compose/1", manifest));
    }

    [Fact]
    public void ReturnsNullWhenNothingExplainsThePath()
    {
        var manifest = ManifestWithMount("/config", "/volume1/docker/app/config");

        // Better to admit the path cannot be resolved than to guess a location and write there.
        Assert.Null(RestoreService.TranslateThroughBindMounts("/data/compose/1", manifest));
    }

    [Fact]
    public void IgnoresARootMount()
    {
        // A mount at "/" would match every path and translate all of them, which is never useful
        // and is how a restore ends up writing somewhere arbitrary.
        var manifest = ManifestWithMount("/", "/volume1/everything");

        Assert.Null(RestoreService.TranslateThroughBindMounts("/data/compose/1", manifest));
    }
}

/// <summary>
/// A folder captured both as a container's bind mount and as a nominated host folder is one file
/// on disk referenced twice. Summing the entries reported more than the backup occupied — 3.25 GB
/// against 3.13 GB actually written, on a real run.
/// </summary>
public class ManifestSizeTests
{
    [Fact]
    public void CountsEachArchiveFileOnce()
    {
        var shared = new ArchiveEntry
        {
            Kind = ArchiveKind.Bind,
            Source = "/volume1/docker/myapp",
            RelativePath = "binds/myapp__srv.tar.gz",
            SizeBytes = 100,
        };

        var manifest = new BackupManifest
        {
            Containers = [new ContainerBackup { Name = "myapp", Archives = [shared] }],
            // The same file, referenced again as a nominated host folder.
            ExtraPaths = [new ArchiveEntry
            {
                Kind = ArchiveKind.HostPath,
                Source = "/volume1/docker/myapp",
                RelativePath = "binds/myapp__srv.tar.gz",
                SizeBytes = 100,
            }],
        };

        Assert.Equal(100, manifest.TotalBytes);
    }

    [Fact]
    public void ExcludesSkippedAndFailedEntries()
    {
        var manifest = new BackupManifest
        {
            Containers =
            [
                new ContainerBackup
                {
                    Name = "myapp",
                    Archives =
                    [
                        new ArchiveEntry { RelativePath = "binds/kept.tar.gz", SizeBytes = 500 },
                        new ArchiveEntry { RelativePath = "", SkipReason = "The source no longer exists.", SizeBytes = 0 },
                        new ArchiveEntry { RelativePath = "binds/failed.tar.gz", Error = "tar exited 2", SizeBytes = 40 },
                    ],
                },
            ],
        };

        // Only what is actually on disk and usable.
        Assert.Equal(500, manifest.TotalBytes);
    }

    [Fact]
    public void SumsDistinctFiles()
    {
        var manifest = new BackupManifest
        {
            Containers =
            [
                new ContainerBackup
                {
                    Name = "myapp",
                    Archives =
                    [
                        new ArchiveEntry { RelativePath = "binds/a.tar.gz", SizeBytes = 10 },
                        new ArchiveEntry { RelativePath = "binds/b.tar.gz", SizeBytes = 32 },
                    ],
                },
            ],
        };

        Assert.Equal(42, manifest.TotalBytes);
    }
}
