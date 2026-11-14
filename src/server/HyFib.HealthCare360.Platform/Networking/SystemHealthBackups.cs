using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>What the backup history says of the test restores, for System health (<see cref="SystemHealthBackups.RestoreTestWarning"/>).</summary>
/// <param name="LastPassedAt">When the newest test restore that passed was made; null when none has.</param>
/// <param name="LastTestedAt">When the newest test restore was made, whatever its outcome; null when none has been.</param>
/// <param name="LastPassed">Whether that one passed.</param>
/// <param name="LastNote">What it found, or why it failed.</param>
/// <param name="LastFile">The backup it restored.</param>
/// <param name="OldestBackupAt">When the oldest verified full backup in the history was made: a new installation has had no night to test one.</param>
internal sealed record RestoreTestHistory(
    DateTimeOffset? LastPassedAt, DateTimeOffset? LastTestedAt, bool? LastPassed, string? LastNote, string? LastFile, DateTimeOffset? OldestBackupAt);

/// <summary>
/// How the backups are set up and checked, on System health (DED-16, DED-17). Nightly backups switched off leave the
/// hospital's data with no backup at all, and every other backup warning silent: critical, which tells IT on-call
/// (<see cref="SystemHealthAlerts"/>). No second disk or NAS leaves every backup on the server's own disk: a warning.
/// Where two or more disks take turns as the second copy (<see cref="BackupDisk"/>), the disk in place past
/// <see cref="BackupOptions.DiskRotationDays"/> leaves no copy away from the building but the off-site one: a warning.
/// No test restore that passed for <see cref="BackupTestRestoreOptions.WarnAfterDays"/> means nothing shows the backups
/// would restore (<see cref="BackupRestoreTest"/>), and a copy on the second disk that no longer reads back as made is no
/// copy (<see cref="BackupUpkeep"/>): warnings both. Nothing is said on a training or test server
/// (<see cref="PlatformOptions.AllowsSimulators"/>), whose data is not the hospital's.
/// </summary>
internal static class SystemHealthBackups
{
    public const string DisabledCode = "backup.disabled";
    public const string NoSecondCopyCode = "backup.no_second_copy";
    public const string DiskNotSwappedCode = "backup.disk_not_swapped";
    public const string RestoreTestCode = "backup.restore_test";
    public const string CopyDamagedCode = "backup.copy_damaged";

    /// <summary>How far back the copies are read to tell whether disks take turns: two or more disks with copies in it.</summary>
    public static readonly TimeSpan RotationSeenWithin = TimeSpan.FromDays(90);

    public static async Task<IReadOnlyList<HealthWarning>> WarningsAsync(HospitalDbContext db, PlatformOptions options, DateTimeOffset now, CancellationToken ct)
    {
        var warnings = new List<HealthWarning>(SetUpWarnings(options.Backup, options.AllowsSimulators));
        if (options.AllowsSimulators || !options.Backup.Enabled)
        {
            return warnings;
        }

        var zone = PlatformServiceCollectionExtensions.ResolveTimeZone(options.TimeZone);
        if (RestoreTestWarning(await RestoreTestHistoryAsync(db, ct), options.Backup.TestRestore, now, zone) is { } untested)
        {
            warnings.Add(untested);
        }

        if (options.Backup.SecondaryDirectory is not { Length: > 0 })
        {
            return warnings;
        }

        // Every copy of a backup still kept: the longest a backup is kept, whatever its kind.
        var since = now - TimeSpan.FromDays(options.Backup.RetentionDays);
        var records = await db.Set<BackupRun>().AsNoTracking()
            .Where(r => r.SecondaryCopies != null && r.StartedAt >= since)
            .Select(r => new { r.FileName, r.SecondaryCopies })
            .ToListAsync(ct);
        var copies = records.SelectMany(r => BackupCopies.Read(r.SecondaryCopies).Select(c => (File: r.FileName ?? string.Empty, Copy: c))).ToList();
        if (DiskWarning(copies.Select(c => c.Copy).Where(c => c.CopiedAt >= now - RotationSeenWithin), options.Backup.DiskRotationDays, now, zone) is { } notSwapped)
        {
            warnings.Add(notSwapped);
        }

        if (DamagedCopiesWarning(copies, options.Backup.CopyCheckDays, now) is { } damaged)
        {
            warnings.Add(damaged);
        }

        return warnings;
    }

    /// <summary>
    /// The test restores' warning for <paramref name="history"/> at <paramref name="now"/>, with times in the hospital's
    /// <paramref name="zone"/>: once none has passed for <see cref="BackupTestRestoreOptions.WarnAfterDays"/>, with the
    /// last one's outcome; while none has been tried, only once the oldest backup is that old, so a new installation has
    /// had its nights to try; and while test restores are switched off.
    /// </summary>
    public static HealthWarning? RestoreTestWarning(RestoreTestHistory history, BackupTestRestoreOptions settings, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
        {
            return new(RestoreTestCode, "warning",
                "Test restores are switched off (HealthCare360:Backup:TestRestore:Enabled is false in hc360.settings.json): nothing checks that the backups " +
                "would restore until someone restores one by hand. Switch them back on and restart the HC360Server service.");
        }

        var limit = TimeSpan.FromDays(Math.Max(settings.WarnAfterDays, 1));
        if (history.LastPassedAt is { } passed && now - passed <= limit)
        {
            return null;
        }

        if (history.LastTestedAt is not { } tested)
        {
            return history.OldestBackupAt is { } oldest && now - oldest > limit
                ? new(RestoreTestCode, "warning",
                    "No backup has been test-restored yet, so nothing shows the backups would restore. Once a week, during the backups' nightly upkeep " +
                    "(03:30), HC360 restores the newest backup into a scratch database and checks it holds what it should: check that the server that runs " +
                    "the background jobs is up.")
                : null;
        }

        var none = history.LastPassedAt is { } p
            ? $"No backup has passed a test restore since {Local(p, zone)}"
            : "No backup has passed a test restore yet";
        var last = history.LastPassed == false
            ? $"The last test, of {history.LastFile} on {Local(tested, zone)}, failed: {Sentence(history.LastNote)} A test runs again each night until one passes."
            : $"None has been tried since {Local(tested, zone)}: check that the server that runs the background jobs is up.";
        return new(RestoreTestCode, "warning",
            $"{none}, so nothing shows the backups would restore. {last} The deployment guide (section 8, \"Test restores\") says what to do.");
    }

    /// <summary>
    /// The copies on the second disk or NAS whose last read-back, within the last two rounds of checks, found them no longer
    /// matching their backups, and that could not be copied again from the server: a warning naming them. A damaged copy
    /// still on the disk is read back again each round, and stays named; one retention removed is not, and drops out.
    /// </summary>
    public static HealthWarning? DamagedCopiesWarning(IEnumerable<(string File, BackupCopy Copy)> copies, int copyCheckDays, DateTimeOffset now)
    {
        var recent = now - (2 * TimeSpan.FromDays(Math.Max(copyCheckDays, 1)));
        var damaged = copies.Where(c => c.Copy.Problem is not null && c.Copy.CheckedAt >= recent).OrderByDescending(c => c.Copy.CheckedAt).ToList();
        if (damaged.Count == 0)
        {
            return null;
        }

        var named = string.Join(", ", damaged.Take(3).Select(d => d.Copy.Disk is { } disk ? $"{d.File} on disk {disk}" : d.File))
                    + (damaged.Count > 3 ? string.Create(CultureInfo.InvariantCulture, $" and {damaged.Count - 3} more") : string.Empty);
        var copiesOf = damaged.Count == 1 ? "A copy" : string.Create(CultureInfo.InvariantCulture, $"{damaged.Count} copies");
        return new(CopyDamagedCode, "warning",
            $"{copiesOf} on the second disk or NAS no longer read back as made: {named}. {Sentence(damaged[0].Copy.Problem)} Replace the disk, or check " +
            "the NAS's own disks, then take a backup under Administration › Backups (Back up now).");
    }

    private static async Task<RestoreTestHistory> RestoreTestHistoryAsync(HospitalDbContext db, CancellationToken ct)
    {
        var runs = db.Set<BackupRun>().AsNoTracking();
        var lastPassedAt = await runs.Where(r => r.RestoreTestPassed == true).MaxAsync(r => r.RestoreTestedAt, ct);
        var last = await runs.Where(r => r.RestoreTestedAt != null)
            .OrderByDescending(r => r.RestoreTestedAt)
            .Select(r => new { r.RestoreTestedAt, r.RestoreTestPassed, r.RestoreTestNote, r.FileName })
            .FirstOrDefaultAsync(ct);
        var oldest = await runs
            .Where(r => r.Status == BackupStatus.Succeeded && r.VerifiedAt != null && (r.Kind == BackupKind.Scheduled || r.Kind == BackupKind.Manual))
            .MinAsync(r => (DateTimeOffset?)r.StartedAt, ct);
        return new RestoreTestHistory(lastPassedAt, last?.RestoreTestedAt, last?.RestoreTestPassed, last?.RestoreTestNote, last?.FileName, oldest);
    }

    private static string Local(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Sentence(string? text)
    {
        var trimmed = text?.Trim();
        return trimmed is null or { Length: 0 } ? string.Empty : trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }

    /// <summary>Backups switched off (critical), or no second copy set (a warning); nothing on a training or test server.</summary>
    public static IReadOnlyList<HealthWarning> SetUpWarnings(BackupOptions backup, bool allowsSimulators)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (allowsSimulators)
        {
            return [];
        }

        if (!backup.Enabled)
        {
            return
            [
                new(DisabledCode, "critical",
                    "Nightly backups are switched off (HealthCare360:Backup:Enabled is false in hc360.settings.json): no backup is made each night, so a disk " +
                    "failure, ransomware or a mistake loses everything since the last one, and System health checks none of the backups. Switch them back " +
                    "on and restart the HC360Server service."),
            ];
        }

        return backup.SecondaryDirectory is { Length: > 0 }
            ? []
            : [
                new(NoSecondCopyCode, "warning",
                    "Backups are not copied off the server's own disk: no second disk or NAS is set (HealthCare360:Backup:SecondaryDirectory). If that disk " +
                    "fails, every backup goes with the database. Connect a USB disk or a NAS share and run Install-HC360Server.ps1 again with " +
                    "-SecondaryBackupDir, as the deployment guide says (section 8)."),
            ];
    }

    /// <summary>
    /// The disk that took the newest copies, where two or more disks have taken copies: a warning once it has taken every
    /// copy for longer than <paramref name="rotationDays"/>, counted from the first of them, with times in the hospital's
    /// <paramref name="zone"/>. Nothing while no disk takes copies (one away, none in: the failed copy's own warning says
    /// so, <see cref="BackupCopies"/>), and nothing with <paramref name="rotationDays"/> 0.
    /// </summary>
    public static HealthWarning? DiskWarning(IEnumerable<BackupCopy> copies, int rotationDays, DateTimeOffset now, TimeZoneInfo zone)
    {
        var known = copies.Where(c => c.Disk is not null).OrderByDescending(c => c.CopiedAt).ToList();
        if (rotationDays <= 0 || known.Select(c => c.Disk).Distinct().Count() < 2 || now - known[0].CopiedAt > BackupService.StaleAfter)
        {
            return null;
        }

        var disk = known[0].Disk;
        var since = known.TakeWhile(c => c.Disk == disk).Last().CopiedAt;
        var inPlace = now - since;
        if (inPlace <= TimeSpan.FromDays(rotationDays))
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(since, zone).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        var days = ((int)inPlace.TotalDays).ToString(CultureInfo.InvariantCulture);
        return new(DiskNotSwappedCode, "warning",
            $"The backup disk {disk} has taken every backup since {local}, {days} days, longer than the {rotationDays.ToString(CultureInfo.InvariantCulture)} " +
            "days after which the disks are swapped: swap it for the other backup disk, and keep this one away from the server, so that a fire, a theft or " +
            "ransomware cannot take every copy at once (HealthCare360:Backup:DiskRotationDays).");
    }
}
