using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// What the backup history records of the full backups' copies to the secondary directory (<see cref="BackupCopies"/>).
/// </summary>
/// <param name="LatestAt">When the latest full backup that succeeded was started.</param>
/// <param name="LatestCopied">Whether that backup was copied.</param>
/// <param name="LatestNote">That backup's note (<see cref="BackupRun.Error"/>), which says why its copy failed when one failed.</param>
/// <param name="LastCopiedAt">When the latest full backup that was copied was started; null when none was.</param>
/// <param name="FirstFailedCopyAt">When the first full backup since then whose copy failed was started; null when none failed.</param>
internal sealed record BackupCopyHistory(DateTimeOffset LatestAt, bool LatestCopied, string? LatestNote, DateTimeOffset? LastCopiedAt, DateTimeOffset? FirstFailedCopyAt);

/// <summary>
/// Whether the backups leave the server's own disk (DED-16). Each backup is copied to the secondary directory, a USB disk
/// or a NAS share; when the disk is unplugged or the NAS is down, the backup is made all the same and only its copy
/// fails. Should the server's disk fail, the newest backup that survives is the newest copy, so the copy's age is the
/// recovery point that matters there (REL-02 asks 24 hours).
/// <list type="bullet">
/// <item>The latest full backup not copied is a warning on System health (<see cref="Code"/>), and its administrators are
/// told in the app when it is made (<see cref="BackupService"/>).</item>
/// <item>No copy for <see cref="UrgentAge"/> is critical, which is told to IT on-call by SMS and escalated
/// (<see cref="SystemHealthAlerts"/>).</item>
/// </list>
/// Only full backups count: the one taken before an update holds no documents, so its copy does not replace a full one,
/// and its missing copy (an update in the day, with the disk out for the swap) does not make the night's full copy
/// missing. Nothing is said with nightly backups switched off, as of the backups themselves (<c>backup.stale</c>).
/// Whether a secondary directory is set is read from the settings of the server building the report: with several
/// servers, every one names the same (DEPLOYMENT.md section 8).
/// </summary>
internal static class BackupCopies
{
    public const string Code = "backup.copy_failed";

    /// <summary>The title of the in-app notification of a backup that was not copied.</summary>
    public const string NotCopiedTitle = "Backup not copied";

    /// <summary>
    /// How long without a copy the warning turns critical: three nights' copies missed, and the half day <c>backup.stale</c>
    /// also allows, so the alert does not race the night's backup that would clear it. A disk taken out on a Friday and
    /// not put back is told to IT on-call on the Monday, in the afternoon.
    /// </summary>
    public static readonly TimeSpan UrgentAge = TimeSpan.FromDays(3.5);

    private const string Fix = "Connect the backup disk, or check the NAS share, then take a backup under Administration › Backups (Back up now).";

    /// <summary>The warning, from the backup history; null when the copies are made, or there is nothing to copy them to.</summary>
    public static async Task<HealthWarning?> WarningAsync(HospitalDbContext db, PlatformOptions options, DateTimeOffset now, CancellationToken ct)
    {
        if (!options.Backup.Enabled || options.Backup.SecondaryDirectory is not { Length: > 0 } secondary)
        {
            return null;
        }

        var full = db.Set<BackupRun>().AsNoTracking()
            .Where(r => r.Status == BackupStatus.Succeeded && (r.Kind == BackupKind.Scheduled || r.Kind == BackupKind.Manual));
        var latest = await full.OrderByDescending(r => r.StartedAt).Select(r => new { r.StartedAt, r.SecondaryCopied, r.Error }).FirstOrDefaultAsync(ct);
        if (latest is null || latest.SecondaryCopied)
        {
            return null;
        }

        var lastCopiedAt = await full.Where(r => r.SecondaryCopied).MaxAsync(r => (DateTimeOffset?)r.StartedAt, ct);
        var failed = full.Where(r => r.Error != null && r.Error.Contains(BackupService.CopyFailedNote));
        if (lastCopiedAt is { } copied)
        {
            failed = failed.Where(r => r.StartedAt > copied);
        }

        var firstFailedAt = await failed.MinAsync(r => (DateTimeOffset?)r.StartedAt, ct);
        var history = new BackupCopyHistory(latest.StartedAt, LatestCopied: false, latest.Error, lastCopiedAt, firstFailedAt);
        return Warning(secondary, history, now, PlatformServiceCollectionExtensions.ResolveTimeZone(options.TimeZone));
    }

    /// <summary>
    /// The warning for <paramref name="history"/> at <paramref name="now"/>, with times in the hospital's
    /// <paramref name="zone"/>. Critical once the newest copy is older than <see cref="UrgentAge"/>, or, where no backup
    /// was ever copied, once copies have failed for that long. A backup whose copy was never attempted (made before the
    /// secondary directory was set) is only a warning, however old: nothing has failed yet.
    /// </summary>
    public static HealthWarning? Warning(string secondary, BackupCopyHistory history, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (history.LatestCopied)
        {
            return null;
        }

        var reason = Reason(history.LatestNote);
        var lastAttempt = reason is null ? string.Empty : $" Last attempt: {reason}";
        if (history.LastCopiedAt is { } copied && now - copied > UrgentAge)
        {
            return new(Code, "critical",
                $"No backup has been copied to {secondary} since the one of {Local(copied, zone)}, which is the newest backup off the server's own disk.{lastAttempt} {Fix}");
        }

        if (history.LastCopiedAt is null && history.FirstFailedCopyAt is { } failing && now - failing > UrgentAge)
        {
            return new(Code, "critical",
                $"No backup has ever been copied to {secondary}, and every copy since {Local(failing, zone)} failed: no backup is off the server's own disk.{lastAttempt} {Fix}");
        }

        return new(Code, "warning", reason is null
            ? $"The latest backup, of {Local(history.LatestAt, zone)}, has no copy in {secondary}: none was attempted, as the server that made it had no secondary directory set. Take a backup under Administration › Backups (Back up now)."
            : $"The backup of {Local(history.LatestAt, zone)} was not copied to {secondary}, so it is on the server's own disk only: {reason} {Fix}");
    }

    /// <summary>The body of the in-app notification of <paramref name="fileName"/>, which was not copied.</summary>
    public static string NotCopiedBody(string fileName, string secondary, string? note) =>
        $"{fileName} was made, but not copied to {secondary}, so it is on the server's own disk only.{(Reason(note) is { } reason ? " " + reason : string.Empty)} {Fix}";

    /// <summary>Why the copy failed, from a backup's note; null when no copy failed.</summary>
    internal static string? Reason(string? note)
    {
        var at = note?.IndexOf(BackupService.CopyFailedNote, StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return null;
        }

        var reason = note![(at + BackupService.CopyFailedNote.Length)..].Trim();
        return reason.Length == 0 ? null : reason.EndsWith('.') ? reason : reason + ".";
    }

    private static string Local(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}
