using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// How the backups are set up, on System health (DED-16, DED-17). Nightly backups switched off leave the hospital's data
/// with no backup at all, and every other backup warning silent: critical, which tells IT on-call
/// (<see cref="SystemHealthAlerts"/>). No second disk or NAS leaves every backup on the server's own disk: a warning.
/// Where two or more disks take turns as the second copy (<see cref="BackupDisk"/>), the disk in place past
/// <see cref="BackupOptions.DiskRotationDays"/> leaves no copy away from the building but the off-site one: a warning.
/// Nothing is said on a training or test server (<see cref="PlatformOptions.AllowsSimulators"/>), whose data is not the
/// hospital's.
/// </summary>
internal static class SystemHealthBackups
{
    public const string DisabledCode = "backup.disabled";
    public const string NoSecondCopyCode = "backup.no_second_copy";
    public const string DiskNotSwappedCode = "backup.disk_not_swapped";

    /// <summary>How far back the copies are read to tell whether disks take turns: two or more disks with copies in it.</summary>
    public static readonly TimeSpan RotationSeenWithin = TimeSpan.FromDays(90);

    public static async Task<IReadOnlyList<HealthWarning>> WarningsAsync(HospitalDbContext db, PlatformOptions options, DateTimeOffset now, CancellationToken ct)
    {
        var warnings = new List<HealthWarning>(SetUpWarnings(options.Backup, options.AllowsSimulators));
        if (!options.AllowsSimulators && options.Backup is { Enabled: true, SecondaryDirectory.Length: > 0, DiskRotationDays: > 0 })
        {
            var since = now - RotationSeenWithin;
            var records = await db.Set<BackupRun>().AsNoTracking()
                .Where(r => r.SecondaryCopies != null && r.StartedAt >= since)
                .Select(r => r.SecondaryCopies)
                .ToListAsync(ct);
            if (DiskWarning(records.SelectMany(BackupCopies.Read), options.Backup.DiskRotationDays, now,
                    PlatformServiceCollectionExtensions.ResolveTimeZone(options.TimeZone)) is { } notSwapped)
            {
                warnings.Add(notSwapped);
            }
        }

        return warnings;
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
