using SshDockerBackup.Core.Backup;
using SshDockerBackup.Core.Docker;
using SshDockerBackup.Core.Models;
using Xunit;

namespace SshDockerBackup.Core.Tests;

/// <summary>
/// What a backup does and does not take. Every case here came from a real host: a container
/// pointing at six folders that had been deleted, a media share bind-mounted read-only into an
/// app, and mounts of host plumbing that were never container data.
/// </summary>
public class SourceSelectionTests
{
    private static BackupOptions NoExclusions() => new();

    [Fact]
    public void ADirectoryIsArchivedAsADirectory()
    {
        var (issue, reason, isDirectory) =
            BackupService.Classify("/volume1/docker/myapp", RemotePathKind.Directory, NoExclusions());

        Assert.Null(issue);
        Assert.Null(reason);
        Assert.True(isDirectory);
    }

    /// <summary>
    /// tar cannot chdir into a file, so a single-file bind mount has to be archived by name from
    /// its parent. Getting this wrong is silent: the archive simply fails and a certificate or key
    /// never gets backed up.
    /// </summary>
    [Fact]
    public void ASingleFileIsArchivedAsAFile()
    {
        var (issue, _, isDirectory) =
            BackupService.Classify("/etc/ssl/site.crt", RemotePathKind.File, NoExclusions());

        Assert.Null(issue);
        Assert.False(isDirectory);
    }

    [Fact]
    public void AMissingSourceIsSkippedRatherThanFailed()
    {
        var (issue, reason, _) =
            BackupService.Classify("/volume1/gone", RemotePathKind.Missing, NoExclusions());

        // Not an error: a container can legitimately reference data that has been moved away.
        // Its configuration is still captured, so it stays rebuildable.
        Assert.Equal(SourceIssueKind.Missing, issue);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/etc/localtime")]
    [InlineData("/proc/sys/net")]
    [InlineData("/sys/fs/cgroup")]
    [InlineData("/dev/dri")]
    public void HostPlumbingIsNeverArchived(string path)
    {
        // Restoring /etc/localtime would rewrite the host's own timezone, and a socket has
        // nothing to capture in the first place.
        var (issue, _, _) = BackupService.Classify(path, RemotePathKind.Directory, NoExclusions());

        Assert.Equal(SourceIssueKind.SystemPlumbing, issue);
    }

    [Fact]
    public void ASocketOrDeviceIsSkipped()
    {
        var (issue, _, _) =
            BackupService.Classify("/volume1/app/some.sock", RemotePathKind.Special, NoExclusions());

        Assert.Equal(SourceIssueKind.Special, issue);
    }

    [Fact]
    public void AnExcludedPathIsSkippedAndSaysWhy()
    {
        var options = new BackupOptions { ExcludedPaths = ["/volume1/photo"] };

        var (issue, reason, _) =
            BackupService.Classify("/volume1/photo", RemotePathKind.Directory, options);

        // Reported apart from failures, because it is a deliberate choice rather than a problem.
        Assert.Equal(SourceIssueKind.Excluded, issue);
        Assert.NotNull(reason);
    }

    [Fact]
    public void ExclusionBeatsAnOtherwisePerfectlyGoodSource()
    {
        var options = new BackupOptions { ExcludedPaths = ["/volume1/media"] };

        // A subfolder of an excluded path is excluded too: the point of excluding a media library
        // is not to have to enumerate what is inside it.
        var (issue, _, _) =
            BackupService.Classify("/volume1/media/movies/2026", RemotePathKind.Directory, options);

        Assert.Equal(SourceIssueKind.Excluded, issue);
    }
}

/// <summary>
/// Exclusions leave data out of a backup, so the matching has to be exact about what it covers.
/// A prefix match that is too eager silently drops a sibling folder.
/// </summary>
public class ExclusionMatchingTests
{
    private static BackupOptions With(params string[] excluded) => new() { ExcludedPaths = [.. excluded] };

    [Fact]
    public void MatchesThePathItself() =>
        Assert.True(With("/volume1/photo").IsExcluded("/volume1/photo"));

    [Fact]
    public void MatchesEverythingBeneathIt() =>
        Assert.True(With("/volume1/photo").IsExcluded("/volume1/photo/2026/holiday"));

    /// <summary>
    /// The bug a naive StartsWith would introduce: "/volume1/photos" is a different share from
    /// "/volume1/photo", and excluding one must not drop the other.
    /// </summary>
    [Fact]
    public void DoesNotMatchASiblingWithASharedPrefix()
    {
        var options = With("/volume1/photo");

        Assert.False(options.IsExcluded("/volume1/photos"));
        Assert.False(options.IsExcluded("/volume1/photo-archive"));
    }

    [Fact]
    public void IgnoresTrailingSlashesOnEitherSide()
    {
        Assert.True(With("/volume1/photo/").IsExcluded("/volume1/photo"));
        Assert.True(With("/volume1/photo").IsExcluded("/volume1/photo/"));
    }

    [Fact]
    public void IgnoresBlankAndWhitespaceEntries()
    {
        // A list edited by hand collects empty lines. One must never exclude everything.
        var options = With("", "   ", "/volume1/photo");

        Assert.False(options.IsExcluded("/volume1/docker/myapp"));
        Assert.True(options.IsExcluded("/volume1/photo"));
    }

    [Fact]
    public void AnEmptyListExcludesNothing()
    {
        var options = new BackupOptions();

        Assert.False(options.IsExcluded("/volume1/docker/myapp"));
        Assert.False(options.IsExcluded(""));
    }
}
