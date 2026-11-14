using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// A full backup taken at once after a change that the backups taken before do not carry (SRS DED-16, DED-20), rather than
/// at the next night's: a rotation of the data encryption key, which a standby or replacement hardware can read data
/// encrypted with only once a backup has carried the key there; and a failover, after which the promoted server has no
/// backup of its own and runs on a new timeline. Each is backed up once: a backup tried after the change, whether it
/// succeeded or failed, settles it, and the nightly backups and their warnings take over from there.
/// </summary>
public static class BackupAfterChange
{
    /// <summary>
    /// Why a full backup is due, given when one was last tried (<paramref name="lastAttemptAt"/>), since when the current
    /// data key has been current (null for the original key) and when this server was promoted (null when it was not);
    /// null when none is.
    /// </summary>
    public static string? Reason(DateTimeOffset? lastAttemptAt, DateTimeOffset? dataKeySince, DateTimeOffset? promotedAt)
    {
        if (promotedAt is { } promoted && !(lastAttemptAt >= promoted))
        {
            return "this server was promoted from the warm standby, and has no backup since";
        }

        return dataKeySince is { } rotated && !(lastAttemptAt >= rotated)
            ? "the data encryption key was rotated, and no backup carries the new key yet"
            : null;
    }
}

/// <summary>
/// Checks every five minutes, on the server that runs the jobs, whether a backup is due after a change
/// (<see cref="BackupAfterChange"/>), and takes it.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class BackupAfterChangeJob(
    BackupService backups,
    IDbContextFactory<HospitalDbContext> contexts,
    IFieldKeyRing ring,
    IOptions<PlatformOptions> options,
    DataDirectories directories,
    TimeProvider time,
    ILogger<BackupAfterChangeJob> logger) : IJob
{
    public const string Schedule = "0 2/5 * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        if (await DueAsync(cancellationToken) is { } reason)
        {
            logger.LogWarning("Taking a full backup now: {Reason}", reason);
            await backups.RunAsync(BackupKind.Scheduled, null, cancellationToken);
        }
    }

    /// <summary>Why a backup is due now; null when none is, or one is under way.</summary>
    private async Task<string?> DueAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var full = db.Set<BackupRun>().AsNoTracking().Where(r => r.Kind == BackupKind.Scheduled || r.Kind == BackupKind.Manual);
        var recent = time.GetUtcNow().AddHours(-3);
        if (await full.AnyAsync(r => r.Status == BackupStatus.Running && r.StartedAt > recent, ct))
        {
            return null;
        }

        var lastAttemptAt = await full.MaxAsync(r => (DateTimeOffset?)r.StartedAt, ct);
        var records = PrimaryFence.Read(directories, options.Value);
        var promotedAt = records.Count == 0
            ? null
            : PrimaryFence.PromotionOf(await PrimaryFence.ReadClusterAsync(db, ct), Environment.MachineName, records)?.PromotedAt;
        return BackupAfterChange.Reason(lastAttemptAt, ring.CurrentSince, promotedAt);
    }
}
