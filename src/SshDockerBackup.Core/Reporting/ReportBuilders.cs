using SshDockerBackup.Core.Backup;
using SshDockerBackup.Core.Models;

namespace SshDockerBackup.Core.Reporting;

/// <summary>
/// Turns the technical state of a backup or restore into something readable. The rule throughout:
/// the overview must make sense to someone who does not know how Docker stores anything, and the
/// detail sections carry the specifics for when they do want them.
/// </summary>
public static class ReportBuilders
{
    private const string SecurityNote =
        "Container settings are saved exactly as they are, and that includes any passwords, API keys " +
        "or tokens the containers were given. They are readable by anyone who can open the backup " +
        "folder. Keep it somewhere private, and encrypt it before it goes off-site or into cloud storage.";

    private const string NotCoveredIntro =
        "This tool backs up Docker. It does not back up the NAS itself, so these stay your responsibility:";

    private static void AddNotCovered(OperationReport report)
    {
        report.Section("What this does not cover", NotCoveredIntro)
            .Add("DSM settings: users, groups, shared folder permissions, network configuration. " +
                 "Use Control Panel > Update & Restore > Configuration Backup.", ReportSeverity.Action)
            .Add("Certificates. Export them from Control Panel > Security > Certificate.", ReportSeverity.Action)
            .Add("Reverse proxy rules, firewall rules and router port forwarding.", ReportSeverity.Action)
            .Add("Files outside your containers — photos, documents, everything in your other shared " +
                 "folders. That is Hyper Backup's job.", ReportSeverity.Action);
    }

    public static OperationReport BeforeBackup(
        PreflightReport preflight, BackupOptions options, int containerCount, string destinationDescription)
    {
        var report = new OperationReport(
            "Before this backup runs",
            $"{containerCount} container(s) to {destinationDescription}");

        report.Headline(
            $"About {BackupProgress.FormatBytes(preflight.EstimatedBytes)} will be copied. That is measured " +
            "uncompressed, so the finished backup will be this size or smaller.");

        if (preflight.FreeSpaceKnown)
        {
            report.Headline(
                $"There is {BackupProgress.FormatBytes(preflight.DestinationFreeBytes)} free at the destination.",
                preflight.MightNotFit ? ReportSeverity.Warning : ReportSeverity.Good);

            if (preflight.MightNotFit)
            {
                report.Headline(
                    "That might not be enough. Compression may still save it if your data is mostly text or " +
                    "databases, but photos and video barely shrink at all.", ReportSeverity.Warning);
            }
        }
        else
        {
            report.Headline(
                "Free space at the destination could not be checked. That is normal for a network path.",
                ReportSeverity.Info);
        }

        // Something you excluded is not a problem the report should nag about, so it is counted and
        // reported apart from the paths that genuinely cannot be copied.
        var excluded = preflight.Issues.Where(i => i.Kind == SourceIssueKind.Excluded).ToList();
        var unusable = preflight.Issues.Where(i => i.Kind != SourceIssueKind.Excluded).ToList();

        if (unusable.Count > 0)
        {
            report.Headline(
                $"{unusable.Count} of {preflight.SourcesChecked} things cannot be copied. This is " +
                "usually expected rather than a problem — see the details below.", ReportSeverity.Warning);
        }
        else
        {
            report.Headline("Everything selected can be copied.", ReportSeverity.Good);
        }

        if (excluded.Count > 0)
        {
            report.Headline(
                $"{excluded.Count} folder(s) are being left out because you excluded them. Their contents " +
                "will not be in this backup.", ReportSeverity.Action);
        }

        var included = report.Section("What gets saved",
            "A container is really three separate things, and all three are captured:");

        included
            .Add("The data your containers store — databases, settings, uploads.", ReportSeverity.Good)
            .Add("How each container was set up — its image, ports, environment and so on — so it can be " +
                 "rebuilt exactly.", ReportSeverity.Good)
            .Add("The network layout, including fixed addresses, so containers can still find each other.",
                ReportSeverity.Good);

        if (options.ExtraPaths.Count > 0)
        {
            included.Add(
                $"{options.ExtraPaths.Count} project folder(s) you nominated, holding things like compose " +
                "files and .env files that no container actually mounts.", ReportSeverity.Good);
        }

        included.Add(options.ImageMode switch
        {
            ImageBackupMode.All => "Every container image. Large, but the backup will work with no internet.",
            ImageBackupMode.None => "No container images at all. Anything you built yourself would be lost.",
            _ => "Only images that cannot be downloaded again. Ones from the internet are left out, since " +
                 "they can simply be fetched when needed.",
        }, options.ImageMode == ImageBackupMode.None ? ReportSeverity.Warning : ReportSeverity.Good);

        included.Add(options.StopContainersDuringBackup
            ? "Containers are paused while their data is copied, then started again. This is what stops a " +
              "database being copied halfway through a write."
            : "Containers keep running while their data is copied. A database copied this way is often " +
              "unusable, and you would only find out when you tried to restore it.",
            options.StopContainersDuringBackup ? ReportSeverity.Good : ReportSeverity.Warning);

        if (excluded.Count > 0)
        {
            var left = report.Section("What you have chosen to leave out",
                "These are on your exclusion list, so they are being skipped on purpose.");

            foreach (var issue in excluded.DistinctBy(i => i.Source, StringComparer.Ordinal))
                left.Add($"{issue.Source} (used by {issue.Owner})", ReportSeverity.Action);

            left.Add(
                "Whatever is in these folders will not be in this backup, and a restore cannot put it " +
                "back. If it matters, make sure something else is copying it — Hyper Backup, for " +
                "instance — or remove it from the exclusion list on the Backup tab.", ReportSeverity.Action);
        }

        if (unusable.Count > 0)
        {
            var skipped = report.Section("What will be skipped",
                "None of these stop the backup. They are listed so nothing is a surprise afterwards.");

            foreach (var issue in unusable)
            {
                var explanation = issue.Kind switch
                {
                    SourceIssueKind.Missing =>
                        "the folder it points at no longer exists on the NAS",
                    SourceIssueKind.SystemPlumbing =>
                        "this is part of the NAS itself, not container data",
                    _ => "there is nothing here that can be copied",
                };

                skipped.Add($"{issue.Owner}: {issue.Source} — {explanation}.", ReportSeverity.Warning);
            }

            skipped.Add(
                "A container whose data is missing is still saved. Its settings are kept, so it can be " +
                "rebuilt — there is simply no data to put back into it.", ReportSeverity.Info);
        }

        AddNotCovered(report);

        report.Section("Keep this backup safe").Add(SecurityNote, ReportSeverity.Warning);

        return report;
    }

    public static OperationReport AfterBackup(BackupManifest manifest, BackupOptions options)
    {
        var failed = manifest.FailedArchives.ToList();
        var skipped = manifest.SkippedArchives.ToList();

        var report = new OperationReport(
            failed.Count == 0 ? "Backup finished" : "Backup finished with problems",
            manifest.RootPath);

        report.Headline(
            $"{manifest.Containers.Count} container(s) saved, {BackupProgress.FormatBytes(manifest.TotalBytes)} written.",
            failed.Count == 0 ? ReportSeverity.Good : ReportSeverity.Info);

        report.Headline($"It is stored at {manifest.RootPath}.");

        if (failed.Count > 0)
        {
            report.Headline(
                $"{failed.Count} item(s) could not be saved. These are worth looking at.", ReportSeverity.Problem);
        }

        if (skipped.Count > 0)
        {
            report.Headline(
                $"{skipped.Count} item(s) were deliberately not saved. That is expected, not a fault.",
                ReportSeverity.Warning);
        }

        var running = manifest.Containers.Count(c => c.WasRunning);
        report.Headline(
            $"{running} container(s) were running and {manifest.Containers.Count - running} were stopped. " +
            "A restore puts each one back the way it was.");

        var contents = report.Section("What is in the backup");
        contents
            .Add($"{manifest.Containers.Count} container(s), with their settings and their data.")
            .Add($"{manifest.Networks.Count} network(s), including their addresses so containers can still " +
                 "find each other after a restore.");

        if (manifest.ExtraPaths.Count > 0)
        {
            foreach (var extra in manifest.ExtraPaths.Where(e => e.Succeeded))
                contents.Add($"Project folder {extra.Source} ({BackupProgress.FormatBytes(extra.SizeBytes)}).");
        }

        if (options.ComputeChecksums)
        {
            contents.Add(
                "Every archive has a checksum, so a restore can tell whether a file has been damaged before " +
                "it writes anything back.", ReportSeverity.Good);
        }

        if (skipped.Count > 0)
        {
            var section = report.Section("Not saved, and that is expected",
                "A container can point at a folder that has been moved away, or mount part of the NAS that " +
                "was never its data.");

            foreach (var item in skipped)
                section.Add($"{item.Source} — {item.SkipReason}", ReportSeverity.Warning);
        }

        if (failed.Count > 0)
        {
            var section = report.Section("Could not be saved",
                "These were meant to be copied and were not. Worth investigating before you rely on this backup.");

            foreach (var item in failed)
                section.Add($"{item.Source} — {item.Error}", ReportSeverity.Problem);
        }

        AddNotCovered(report);

        report.Section("Keep this backup safe").Add(SecurityNote, ReportSeverity.Warning);

        return report;
    }

    public static OperationReport BeforeRestore(
        BackupManifest manifest,
        IReadOnlyList<ContainerBackup> containers,
        IReadOnlyList<ArchiveEntry> extraPaths,
        RestoreOptions options,
        string targetDescription,
        RestorePreflight? preflight = null)
    {
        var report = new OperationReport("Before this restore runs", $"onto {targetDescription}");

        // Lead with this: an image that is neither on the NAS nor in the backup, on a NAS with no
        // internet, is the one thing that stops a restore dead — and it is knowable in advance.
        if (preflight is not null)
        {
            if (preflight.WillFailOffline)
            {
                report.Headline(
                    $"{preflight.ImagesNeedingDownload.Count} container image(s) are not on the NAS and not in " +
                    "this backup, and the NAS cannot reach the internet. Those containers will fail to start.",
                    ReportSeverity.Problem);
            }
            else if (preflight.DownloadUnverified)
            {
                report.Headline(
                    $"{preflight.ImagesNeedingDownload.Count} container image(s) will have to be downloaded, " +
                    "and it could not be confirmed that the NAS has internet access.", ReportSeverity.Warning);
            }
            else if (preflight.ImagesNeedingDownload.Count > 0)
            {
                report.Headline(
                    $"{preflight.ImagesNeedingDownload.Count} container image(s) will be downloaded. The NAS " +
                    "can reach the internet, so that should be fine.", ReportSeverity.Info);
            }
            else
            {
                report.Headline(
                    "Every image needed is already on the NAS or inside this backup, so no internet is required.",
                    ReportSeverity.Good);
            }
        }

        var willStart = options.PreserveRunningState ? containers.Count(c => c.WasRunning) : containers.Count;
        var willStayStopped = containers.Count - willStart;

        report.Headline($"{containers.Count} container(s) will be rebuilt on {targetDescription}.");

        if (options.RecreateContainers)
        {
            report.Headline(willStayStopped == 0
                ? $"All {willStart} will be started."
                : $"{willStart} will be started and {willStayStopped} will be left stopped, matching how they " +
                  "were when the backup was taken.");
        }
        else
        {
            report.Headline("Containers will not be rebuilt — only their data will be put back.",
                ReportSeverity.Warning);
        }

        if (options.RestoreVolumeData)
        {
            report.Headline(
                "Saved data will be written over what is on the NAS now. Files with the same name are " +
                "replaced; anything newer that is not in the backup is left alone.", ReportSeverity.Warning);
        }
        else
        {
            report.Headline("No data will be written. Only the containers themselves are rebuilt.",
                ReportSeverity.Good);
        }

        if (options.RemoveExistingContainers)
        {
            report.Headline(
                "Any container that already has one of these names will be deleted and rebuilt.",
                ReportSeverity.Warning);
        }

        var writes = report.Section("What will be written");

        if (options.RestoreVolumeData)
        {
            foreach (var container in containers)
            {
                foreach (var archive in container.Archives.Where(a =>
                             a.Kind is ArchiveKind.Volume or ArchiveKind.Bind && a.Succeeded))
                {
                    writes.Add($"{container.Name}: {archive.Source} " +
                               $"({BackupProgress.FormatBytes(archive.SizeBytes)})");
                }
            }
        }

        if (options.RestoreExtraPaths)
        {
            foreach (var extra in extraPaths)
                writes.Add($"Project folder {extra.Source} ({BackupProgress.FormatBytes(extra.SizeBytes)})");
        }

        if (!writes.HasContent)
            writes.Add("Nothing. No data or folders are being written.", ReportSeverity.Good);

        if (options.RecreateContainers)
        {
            var rebuild = report.Section("What will be rebuilt");

            foreach (var container in containers)
            {
                var state = !options.PreserveRunningState || container.WasRunning ? "started" : "left stopped";
                var project = container.ComposeProject is { Length: > 0 } p ? $", in project {p}" : "";
                rebuild.Add($"{container.Name} — {state}{project}");
            }

            if (manifest.Networks.Count > 0)
            {
                rebuild.Add(
                    $"Any of the {manifest.Networks.Count} saved network(s) that are missing will be recreated " +
                    "with their original addresses.", ReportSeverity.Good);
            }
        }

        if (preflight is not null)
        {
            var images = report.Section("Container images",
                "Every container needs its image. It can come from the NAS, from this backup, or from the internet.");

            if (preflight.ImagesAlreadyOnHost.Count > 0)
            {
                images.Add($"{preflight.ImagesAlreadyOnHost.Count} already on the NAS — nothing to do.",
                    ReportSeverity.Good);
            }

            if (preflight.ImagesFromBackup.Count > 0)
            {
                images.Add($"{preflight.ImagesFromBackup.Count} will be loaded from this backup, which is the " +
                           "exact version that was running.", ReportSeverity.Good);
            }

            foreach (var image in preflight.ImagesNeedingDownload)
            {
                images.Add($"{image} must be downloaded from the internet.",
                    preflight.RegistryReachable == false ? ReportSeverity.Problem : ReportSeverity.Warning);
            }

            if (preflight.ImagesNeedingDownload.Count > 0 && preflight.RegistryReachable == false)
            {
                images.Add(
                    "The NAS could not reach a container registry. Either connect it to the internet, or take a " +
                    "new backup with image export set to every image while the containers still run.",
                    ReportSeverity.Action);
            }
        }

        report.Section("Worth knowing", "Things a restore cannot do for you:")
            .Add("Shared folders must already exist in DSM. On a new NAS, create them in Control Panel first " +
                 "or the restored files will land in a plain folder with the wrong permissions.", ReportSeverity.Action);

        return report;
    }

    public static OperationReport AfterRestore(
        RestoreOutcome outcome, IReadOnlyList<ContainerBackup> containers, RestoreOptions options)
    {
        var report = new OperationReport(
            outcome.Failed == 0 ? "Restore finished" : "Restore finished with problems",
            $"{outcome.Restored} restored, {outcome.Failed} failed");

        report.Headline(
            outcome.Failed == 0
                ? $"{outcome.Restored} item(s) were restored successfully."
                : $"{outcome.Restored} item(s) restored, {outcome.Failed} failed.",
            outcome.Failed == 0 ? ReportSeverity.Good : ReportSeverity.Problem);

        if (options.RecreateContainers)
        {
            var started = options.PreserveRunningState ? containers.Count(c => c.WasRunning) : containers.Count;
            report.Headline($"{started} container(s) should now be running.");
        }

        if (outcome.Messages.Count > 0)
        {
            var notes = report.Section("What happened", "One line per thing the restore did:");
            foreach (var message in outcome.Messages)
            {
                var severity = message.Kind switch
                {
                    RestoreNoteKind.Problem => ReportSeverity.Problem,
                    RestoreNoteKind.Note => ReportSeverity.Info,
                    _ => ReportSeverity.Good,
                };

                notes.Add(message.Text, severity);
            }
        }

        report.Section("Check these yourself", "A restore cannot verify that your services actually work:")
            .Add("Open the apps you care about and confirm they load and can see their data.", ReportSeverity.Action)
            .Add("If a project shows grey in Container Manager while its containers are running, open it and " +
                 "use Action > Build once. Synology records a project's own status separately.",
                ReportSeverity.Action)
            .Add("DSM settings, certificates and reverse proxy rules are not part of this restore. Put those " +
                 "back from Synology's own Configuration Backup.", ReportSeverity.Action);

        return report;
    }
}
