using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Settings;

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
/// How long backups are kept (DED-16), wherever HC360 puts them: one policy, with two kinds of place (DEPLOYMENT.md
/// section 8, "How long backups are kept"; docs/design/incremental-backup.md, section 5.7).
/// <list type="bullet">
/// <item><b>Where HC360 removes backups</b>, on the server and on a second disk or NAS it may delete from:
/// grandfather-father-son (<see cref="BackupOptions.Retention"/>), every backup of the newest 14 days that have one, then the
/// last of each of the newest 8 weeks and of the newest 12 months, so a year back there is still a backup to go to, a month
/// apart, rather than nothing past 30 days. A weekly or monthly one is a full backup when the week or month has one, not
/// the database-only backup taken before an update. Counting days, weeks and months that have a backup, rather than
/// calendar ones, keeps the newest backups when backups stop; nothing is kept past
/// <see cref="BackupRetentionOptions.LongestDays"/> all the same, except the newest full backup, which is never removed.
/// Each backup folder keeps every document once, and a document goes only when no backup kept there lists it
/// (<see cref="BackupStore.Prune"/>): one an old monthly backup needs stays as long as that backup.</item>
/// <item><b>Where something else removes them by age</b>, an append-only share (<see cref="BackupOptions.SecondaryAppendOnly"/>)
/// and the off-site bucket: every file is kept a fixed number of days after it was written, the documents in weekly
/// generations (<see cref="BackupStore.GenerationDays"/>), so each backup there restores for that long. Such a place cannot
/// keep only a week's or a month's last backup. The share's own retention keeps each file at least
/// <see cref="AppendOnlyMinimumDays"/> (the daily backups' days and a generation) and at most the longest period; the bucket
/// keeps each backup for its lock (<see cref="BackupOffsiteSettings.RetainDays"/>), its documents a generation longer, and
/// its lifecycle removes them the day after (DEPLOYMENT.md section 8).</item>
/// </list>
/// What the privacy notice and the erasure answer say of erased data in backups is counted from <see cref="DaysAsync"/>
/// (DPDP Act s8(7)), so it never says less than any copy is kept.
/// </summary>
public static class BackupRetention
{
    /// <summary>The longest any copy of a backup is kept, in days: on the server and its second copy, or off site while that is on.</summary>
    public static async Task<int> DaysAsync(ISettingsService settings, PlatformOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        var days = Math.Max(options.Backup.RetentionDays, 1);

        // An append-only second copy keeps every backup for the share's own retention, at least the daily backups' days and
        // a generation (docs/design/incremental-backup.md, sections 5.6 and 5.7).
        if (options.Backup.SecondaryAppendOnly && options.Backup.SecondaryDirectory is { Length: > 0 })
        {
            days = Math.Max(days, AppendOnlyMinimumDays(options.Backup.Retention));
        }

        BackupOffsiteSettings? offsite;
        try
        {
            offsite = await settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return days;
        }

        // The bucket's documents are in generations too: a copy stays until its generation's last backup has expired, a
        // generation after the lock of the backup that first needed it, and the lifecycle removes it the day after.
        return offsite is { Enabled: true, RetainDays: > 0 } ? Math.Max(days, offsite.RetainDays + BackupStore.GenerationDays + 1) : days;
    }

    /// <summary>
    /// The least an append-only share's own retention may keep each file, in days: every backup of the daily window
    /// (<see cref="BackupRetentionOptions.DailyKept"/>) must restore from the share alone, and a generation's documents are
    /// written up to a generation (<see cref="BackupStore.GenerationDays"/>) before its last backup. 21 by default.
    /// </summary>
    public static int AppendOnlyMinimumDays(BackupRetentionOptions retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        return retention.DailyKept + BackupStore.GenerationDays;
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
}
