using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>What System health reads of point-in-time recovery (SRS REL-02).</summary>
/// <param name="Archiving">Whether PostgreSQL archives the WAL (Enable-HC360Replication.ps1 -WalArchive).</param>
/// <param name="Cluster">The database's cluster, while it archives.</param>
/// <param name="NewestBaseBackup">The newest base backup of that cluster; null when there is none.</param>
/// <param name="Status">What the base backups did last (<see cref="PointInTimeRecovery.Load"/>).</param>
internal sealed record ArchiveReport(bool Archiving, DatabaseCluster? Cluster, BaseBackupFile? NewestBaseBackup, PointInTimeRecoveryStatus Status);

/// <summary>
/// Point-in-time recovery on System health (SRS REL-02): while PostgreSQL archives the WAL, a restore to a moment between two
/// nightly backups needs a base backup of the database's current timeline, which HC360 takes every week
/// (<see cref="BaseBackupService"/>). Nothing shows while the WAL is not archived.
/// </summary>
internal static class SystemHealthArchive
{
    /// <summary>The weekly base backup, and a day to spare.</summary>
    public static readonly TimeSpan BaseBackupStaleAfter = TimeSpan.FromDays(8);

    public static async Task<IReadOnlyList<HealthWarning>> WarningsAsync(HospitalDbContext db, DataDirectories directories, DateTimeOffset now, CancellationToken ct) =>
        await ReadAsync(db, directories, ct) is { } report ? Warnings(report, now) : [];

    /// <summary>The report; null when PostgreSQL could not be asked (its own warning says so).</summary>
    private static async Task<ArchiveReport?> ReadAsync(HospitalDbContext db, DataDirectories directories, CancellationToken ct)
    {
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                var connection = db.Database.GetDbConnection();
                if (!(await PointInTimeRecovery.ReadArchiveSettingsAsync(connection, ct)).On)
                {
                    return new ArchiveReport(false, null, null, new PointInTimeRecoveryStatus());
                }

                var cluster = await DatabaseCluster.ReadAsync(connection, ct);
                var newest = BaseBackupArchive.List(PointInTimeRecovery.BaseBackupFolder(directories.Backups, cluster.SystemIdentifier)).FirstOrDefault();
                return new ArchiveReport(true, cluster, newest, PointInTimeRecovery.Load(directories));
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
    /// A base backup that failed since the newest one was made; none of the current timeline (archiving just turned on, or
    /// a failover started a new one); or none for over a week.
    /// </summary>
    internal static IReadOnlyList<HealthWarning> Warnings(ArchiveReport report, DateTimeOffset now)
    {
        if (!report.Archiving || report.Cluster is not { } cluster)
        {
            return [];
        }

        var newest = report.NewestBaseBackup;
        if (report.Status is { BaseBackupFailedAt: { } failedAt } status && (newest is null || failedAt > newest.CreatedAt))
        {
            return
            [
                new("pitr.base_backup_failed", "warning",
                    $"The base backup of {SystemHealthService.Since(failedAt, now)} failed: {status.BaseBackupError} A restore to a point in time needs one; " +
                    "HC360 tries again within 6 hours. The nightly backups are not affected."),
            ];
        }

        if (newest is null)
        {
            return
            [
                new("pitr.no_base_backup", "warning",
                    "PostgreSQL archives the WAL, but there is no base backup of the database yet, which a restore to a point in time starts from. " +
                    "HC360 takes one within a quarter of an hour."),
            ];
        }

        if (newest.Timeline != cluster.Timeline)
        {
            return
            [
                new("pitr.no_base_backup", "warning",
                    $"There is no base backup since the database moved to timeline {cluster.Timeline} (after a failover, or a restore to a point in time): " +
                    "the WAL archived since replays only onto a base backup taken after it. HC360 takes one within a quarter of an hour."),
            ];
        }

        return now - newest.CreatedAt > BaseBackupStaleAfter
            ? [new("pitr.base_backup_stale", "warning",
                $"The newest base backup is of {SystemHealthService.Since(newest.CreatedAt, now)}: none was made at the weekly time since. " +
                "A restore to a point in time replays all the WAL archived since the base backup it starts from, so the older it is, the longer that takes.")]
            : [];
    }
}
