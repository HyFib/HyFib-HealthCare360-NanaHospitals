using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>A backup file as retention sees it: when its backup was started, by its name, and whether it is a full backup.</summary>
/// <param name="Full">A nightly or manual backup, with the documents; false for the one taken before an update.</param>
internal sealed partial record BackupFile(string Path, DateTimeOffset StartedAt, bool Full)
{
    /// <summary>
    /// The backup a file name names, <c>hc360-20261114-200000-scheduled.hc360bak</c>: started at 20:00:00 UTC on 14 November
    /// 2026, a nightly backup (<see cref="BackupService.CreateAsync(BackupKind, DateTimeOffset, bool, IReadOnlyList{string}, CancellationToken)"/>);
    /// null for a name HC360 did not give, which retention leaves alone.
    /// </summary>
    public static BackupFile? From(string path)
    {
        var match = NamePattern().Match(System.IO.Path.GetFileName(path));
        return match.Success && DateTime.TryParseExact(match.Groups["at"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var startedAt)
            ? new BackupFile(path, new DateTimeOffset(startedAt, TimeSpan.Zero), match.Groups["kind"].Value != "premigration")
            : null;
    }

    /// <summary>The backups in <paramref name="directory"/>, by their names; files with other names are left out.</summary>
    public static IReadOnlyList<BackupFile> In(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*" + BackupService.FileExtension).Select(From).OfType<BackupFile>()];

    [GeneratedRegex(@"^hc360-(?<at>\d{8}-\d{6})-(?<kind>scheduled|manual|premigration)\.hc360bak$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamePattern();
}

/// <summary>
/// How long backups are kept, wherever HC360 puts them (DED-16). On the server and its second copy (the USB disk or NAS),
/// grandfather-father-son (<see cref="BackupOptions.Retention"/>): every backup of the newest 14 days that have one, then
/// the last of each of the newest 8 weeks and of the newest 12 months, so a year back there is still a backup to go to,
/// a month apart, rather than nothing past 30 days. A weekly or monthly one is a full backup when the week or month has
/// one, not the database-only backup taken before an update. Counting days, weeks and months that have a backup, rather
/// than calendar ones, keeps the newest backups when backups stop; nothing is kept past
/// <see cref="BackupRetentionOptions.LongestDays"/> all the same, except the newest full backup, which is never removed.
/// While the off-site copy is on, the bucket keeps its copies until their lock (<see cref="BackupOffsiteSettings.RetainDays"/>)
/// has passed and its lifecycle removes them the day after (DEPLOYMENT.md section 8). What the privacy notice and the
/// erasure answer say of erased data in backups is counted from <see cref="DaysAsync"/> (DPDP Act s8(7)), so it never
/// says less than any copy is kept.
/// </summary>
public static class BackupRetention
{
    /// <summary>How long an unfinished file (<c>.partial</c>) is left: only a server stopped in the middle of a backup or a copy leaves one.</summary>
    internal static readonly TimeSpan PartialKept = TimeSpan.FromDays(1);

    /// <summary>The longest any copy of a backup is kept, in days: on the server and its second copy, or off site while that is on.</summary>
    public static async Task<int> DaysAsync(ISettingsService settings, PlatformOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        var days = options.Backup.RetentionDays;
        BackupOffsiteSettings? offsite;
        try
        {
            offsite = await settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return days;
        }

        return offsite is { Enabled: true, RetainDays: > 0 } ? Math.Max(days, offsite.RetainDays + 1) : days;
    }

    /// <summary>
    /// The backups of <paramref name="files"/> that <paramref name="retention"/> no longer keeps at <paramref name="now"/>,
    /// with days, weeks (Monday to Sunday) and months as the hospital's calendar has them (<paramref name="zone"/>).
    /// </summary>
    internal static IReadOnlyList<BackupFile> ToRemove(IEnumerable<BackupFile> files, BackupRetentionOptions retention, TimeZoneInfo zone, DateTimeOffset now)
    {
        // Newest first, so each grouping below meets the newest day, week or month first, and each group its newest backup.
        var backups = files.OrderByDescending(f => f.StartedAt).ToList();
        DateOnly Day(BackupFile f) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(f.StartedAt, zone).DateTime);

        var kept = new HashSet<BackupFile>();
        foreach (var day in backups.GroupBy(Day).Take(retention.DailyKept))
        {
            kept.UnionWith(day);
        }

        foreach (var week in backups.GroupBy(f => Day(f).AddDays(-(((int)Day(f).DayOfWeek + 6) % 7))).Take(retention.WeeklyKept))
        {
            kept.Add(Last(week));
        }

        foreach (var month in backups.GroupBy(f => (Day(f).Year, Day(f).Month)).Take(retention.MonthlyKept))
        {
            kept.Add(Last(month));
        }

        var newestFull = backups.FirstOrDefault(f => f.Full);
        var oldest = now - TimeSpan.FromDays(retention.LongestDays);
        return [.. backups.Where(f => f != newestFull && (!kept.Contains(f) || f.StartedAt < oldest))];
    }

    /// <summary>The backup a week or month keeps: its last full backup, or its last one when it has none.</summary>
    private static BackupFile Last(IEnumerable<BackupFile> newestFirst) => newestFirst.FirstOrDefault(f => f.Full) ?? newestFirst.First();

    /// <summary>
    /// Removes from <paramref name="directory"/> the backups retention no longer keeps, and what a server stopped in the
    /// middle of a backup or a copy left (<c>.partial</c> files over a day old). Each removal is tried; the first that
    /// fails is thrown once the others are done, as a share that refuses deletes (DEPLOYMENT.md section 8) refuses them all.
    /// </summary>
    /// <returns>The backups removed.</returns>
    internal static IReadOnlyList<string> Apply(string directory, BackupRetentionOptions retention, TimeZoneInfo zone, DateTimeOffset now, ILogger logger)
    {
        var removed = new List<string>();
        ExceptionDispatchInfo? failed = null;
        var leftovers = Directory.EnumerateFiles(directory, "*" + BackupService.FileExtension + BackupService.PartialExtension)
            .Where(f => File.GetLastWriteTimeUtc(f) < (now - PartialKept).UtcDateTime);
        foreach (var path in ToRemove(BackupFile.In(directory), retention, zone, now).Select(f => f.Path).Concat(leftovers).ToList())
        {
            try
            {
                File.Delete(path);
                removed.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (removed.Count > 0)
        {
            logger.LogInformation("Removed {Count} backup file(s) from {Directory} that retention no longer keeps", removed.Count, directory);
        }

        failed?.Throw();
        return removed;
    }
}
