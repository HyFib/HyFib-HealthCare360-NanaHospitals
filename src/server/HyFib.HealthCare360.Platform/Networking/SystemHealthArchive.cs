using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>What System health reads of point-in-time recovery (SRS REL-02).</summary>
/// <param name="Archiving">Whether PostgreSQL archives the WAL (Enable-HC360Replication.ps1 -WalArchive).</param>
/// <param name="Cluster">The database's cluster, while it archives.</param>
/// <param name="NewestBaseBackup">The newest base backup of that cluster; null when there is none.</param>
/// <param name="Status">What the base backups and the WAL job did last (<see cref="PointInTimeRecovery.Load"/>).</param>
/// <param name="ArchiveFolder">The folder PostgreSQL archives the WAL to, when Enable-HC360Replication.ps1 set it up.</param>
/// <param name="OldestPlainWal">When the oldest WAL file still unencrypted in it was archived, the one held back aside.</param>
/// <param name="Secondary">The secondary directory the encrypted WAL and base backups are copied to; null when none is set.</param>
internal sealed record ArchiveReport(
    bool Archiving,
    DatabaseCluster? Cluster,
    BaseBackupFile? NewestBaseBackup,
    PointInTimeRecoveryStatus Status,
    string? ArchiveFolder = null,
    DateTimeOffset? OldestPlainWal = null,
    string? Secondary = null);

/// <summary>
/// Point-in-time recovery on System health (SRS REL-02): while PostgreSQL archives the WAL, a restore to a moment between two
/// nightly backups needs a base backup of the database's current timeline, which HC360 takes every week
/// (<see cref="BaseBackupService"/>), and every WAL file since, which HC360 encrypts within a minute of PostgreSQL archiving
/// it and copies, encrypted, to the secondary directory (<see cref="WalArchiveService"/>). Nothing shows while the WAL is
/// not archived.
/// </summary>
internal static class SystemHealthArchive
{
    /// <summary>The weekly base backup, and a day to spare.</summary>
    public static readonly TimeSpan BaseBackupStaleAfter = TimeSpan.FromDays(8);

    /// <summary>
    /// How long a WAL file may wait unencrypted, on the server's own disk only, before System health says so, critically:
    /// the recovery point after a disk failure is that old, though PostgreSQL closes a file every quarter of an hour.
    /// </summary>
    public static readonly TimeSpan PlainWalAfter = TimeSpan.FromMinutes(15);

    public static async Task<IReadOnlyList<HealthWarning>> WarningsAsync(
        HospitalDbContext db, DataDirectories directories, PlatformOptions options, DateTimeOffset now, CancellationToken ct) =>
        await ReadAsync(db, directories, options, ct) is { } report ? Warnings(report, now) : [];

    /// <summary>The report; null when PostgreSQL could not be asked (its own warning says so).</summary>
    private static async Task<ArchiveReport?> ReadAsync(HospitalDbContext db, DataDirectories directories, PlatformOptions options, CancellationToken ct)
    {
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var connection = db.Database.GetDbConnection();
                var settings = await PointInTimeRecovery.ReadArchiveSettingsAsync(connection, ct);
                if (!settings.On)
                {
                    return new ArchiveReport(false, null, null, new PointInTimeRecoveryStatus());
                }

                var cluster = await DatabaseCluster.ReadAsync(connection, ct);
                var newest = BaseBackupArchive.List(PointInTimeRecovery.BaseBackupFolder(directories.Backups, cluster.SystemIdentifier)).FirstOrDefault();
                var status = PointInTimeRecovery.Load(directories);
                var folder = WalArchive.ArchiveFolder(settings.Command);
                return new ArchiveReport(true, cluster, newest, status, folder, folder is null ? null : OldestPlain(folder, status.WalConflict),
                    options.Backup.SecondaryDirectory is { Length: > 0 } secondary ? secondary : null);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// When the oldest WAL file in the archive folder was archived, the one held back as a conflict aside; null when there is
    /// none. Windows' copy keeps the time a file was last written and makes the copy's creation time the time of the copy,
    /// so the later of the two is when it was archived.
    /// </summary>
    private static DateTimeOffset? OldestPlain(string folder, string? conflict)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder)
                    .Where(path => WalArchive.IsArchivedName(Path.GetFileName(path)) && Path.GetFileName(path) != conflict)
                    .Select(path => (DateTimeOffset?)new DateTimeOffset(new[] { File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path) }.Max(), TimeSpan.Zero))
                    .Min()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The base backups' warnings (<see cref="BaseBackupWarnings"/>), then the archived WAL's: a WAL file archived again with
    /// other content, WAL left unencrypted on the server's own disk, and copies to the secondary directory that fail.
    /// </summary>
    internal static IReadOnlyList<HealthWarning> Warnings(ArchiveReport report, DateTimeOffset now)
    {
        if (!report.Archiving || report.Cluster is null)
        {
            return [];
        }

        var warnings = new List<HealthWarning>(BaseBackupWarnings(report, now));
        var status = report.Status;
        if (status.WalConflict is { } conflict)
        {
            warnings.Add(new("pitr.wal_conflict", "critical",
                $"PostgreSQL archived the WAL file {conflict} again, with other content than the copy HC360 already encrypted. HC360 keeps both: the new one " +
                $"stays unencrypted in {report.ArchiveFolder}. A restore to a point in time may not replay past it: send PostgreSQL's log and the file's name to HyFib support."));
        }

        if (report.OldestPlainWal is { } oldest && now - oldest > PlainWalAfter)
        {
            var why = status.WalError is { } error ? $" The last attempt failed: {error}" : string.Empty;
            warnings.Add(new("pitr.wal_not_encrypted", "critical",
                $"The WAL PostgreSQL archived since {SystemHealthService.Since(oldest, now)} is not encrypted into the backups yet, so it is on the server's own disk only.{why} " +
                "HC360 encrypts it every minute: check that the HC360 service runs the jobs, and its log. Where access was denied, run Enable-HC360Replication.ps1 -WalArchive again."));
        }

        if (report.Secondary is { } secondary && status.CopyFailingSince is { } failingSince)
        {
            var urgent = now - failingSince > BackupCopies.UrgentAge;
            warnings.Add(new("pitr.wal_not_copied", urgent ? "critical" : "warning",
                $"The encrypted WAL and base backups have not been copied to {secondary} since {SystemHealthService.Since(failingSince, now)}: {status.CopyError} " +
                "They are on the server's own disk only, so a disk failure would lose the changes since. Connect the backup disk, or check the NAS share: HC360 copies them within a minute."));
        }

        return warnings;
    }

    /// <summary>
    /// A base backup that failed since the newest one was made; none of the current timeline (archiving just turned on, or
    /// a failover started a new one); or none for over a week.
    /// </summary>
    private static IEnumerable<HealthWarning> BaseBackupWarnings(ArchiveReport report, DateTimeOffset now)
    {
        var cluster = report.Cluster!;
        var newest = report.NewestBaseBackup;
        if (report.Status is { BaseBackupFailedAt: { } failedAt } status && (newest is null || failedAt > newest.CreatedAt))
        {
            yield return new("pitr.base_backup_failed", "warning",
                $"The base backup of {SystemHealthService.Since(failedAt, now)} failed: {status.BaseBackupError} A restore to a point in time needs one; " +
                "HC360 tries again within 6 hours. The nightly backups are not affected.");
        }
        else if (newest is null)
        {
            yield return new("pitr.no_base_backup", "warning",
                "PostgreSQL archives the WAL, but there is no base backup of the database yet, which a restore to a point in time starts from. " +
                "HC360 takes one within a quarter of an hour.");
        }
        else if (newest.Timeline != cluster.Timeline)
        {
            yield return new("pitr.no_base_backup", "warning",
                $"There is no base backup since the database moved to timeline {cluster.Timeline} (after a failover, or a restore to a point in time): " +
                "the WAL archived since replays only onto a base backup taken after it. HC360 takes one within a quarter of an hour.");
        }
        else if (now - newest.CreatedAt > BaseBackupStaleAfter)
        {
            yield return new("pitr.base_backup_stale", "warning",
                $"The newest base backup is of {SystemHealthService.Since(newest.CreatedAt, now)}: none was made at the weekly time since. " +
                "A restore to a point in time replays all the WAL archived since the base backup it starts from, so the older it is, the longer that takes.");
        }
    }
}
