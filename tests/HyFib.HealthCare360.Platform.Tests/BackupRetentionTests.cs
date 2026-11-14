using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Grandfather-father-son retention of the backups on the server and on the second disk or NAS (DED-16): every backup of
/// the newest 14 days, the last of each of the newest 8 weeks and of the newest 12 months, so a corruption found weeks or
/// months late still has a backup from before it, rather than nothing past 30 days.
/// </summary>
public sealed class BackupRetentionTests : IDisposable
{
    private static readonly BackupRetentionOptions Defaults = new();
    private static readonly DateTimeOffset Now = Ist(2026, 11, 14, 2, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// A year of nightly backups at 01:30 IST (20:00 UTC the day before, as their names say): the 14 newest days, the
    /// Sundays of the 8 newest weeks, and the last day of the 12 newest months are kept; that is 30 backups, the oldest
    /// the last of December, and nothing else.
    /// </summary>
    [Fact]
    public void A_year_of_nightly_backups_keeps_fourteen_days_eight_weeks_and_twelve_months()
    {
        var nightly = Nightly(Ist(2025, 11, 1, 1, 30), Ist(2026, 11, 14, 1, 30));

        var removed = BackupRetention.ToRemove(nightly, Defaults, HospitalTimeZone.Zone, Now);

        var kept = Dates(nightly.Except(removed));
        kept.Count.ShouldBe(30);
        kept.Count(d => d >= new DateOnly(2026, 11, 1)).ShouldBe(14, "every backup of the newest 14 days");
        kept.ShouldContain(new DateOnly(2026, 10, 25), "the Sunday of a week before the daily ones");
        kept.ShouldContain(new DateOnly(2026, 9, 27), "the Sunday of the eighth week, counting this one");
        kept.ShouldNotContain(new DateOnly(2026, 9, 20), "a ninth week back");
        kept.ShouldContain(new DateOnly(2026, 10, 31), "the last of October");
        kept.ShouldContain(new DateOnly(2026, 2, 28), "the last of February");
        kept.Min().ShouldBe(new DateOnly(2025, 12, 31), "the twelfth month, counting this one");
        kept.ShouldNotContain(new DateOnly(2026, 10, 30), "an ordinary night of last month");
    }

    /// <summary>
    /// The backups of a day in the daily window are all kept: one taken by hand before a risky change, and the one taken
    /// before an update, as well as the night's. Past it, a week keeps its last full backup: the one before an update
    /// holds no documents, so it is not the week's backup.
    /// </summary>
    [Fact]
    public void A_week_keeps_its_last_full_backup_and_the_daily_window_keeps_every_backup()
    {
        var today = new[]
        {
            Backup(Ist(2026, 11, 13, 1, 30)),
            Backup(Ist(2026, 11, 13, 11, 5), "manual"),
            Backup(Ist(2026, 11, 13, 14, 0), "premigration"),
        };
        var weekBefore = new[]
        {
            Backup(Ist(2026, 10, 24, 1, 30)),
            Backup(Ist(2026, 10, 25, 1, 30)),
            Backup(Ist(2026, 10, 25, 15, 0), "premigration"),
        };

        var removed = BackupRetention.ToRemove([.. today, .. weekBefore], new BackupRetentionOptions { Daily = 1, Weekly = 8, Monthly = 0 },
            HospitalTimeZone.Zone, Now);

        removed.ShouldBe([weekBefore[2], weekBefore[0]], ignoreOrder: true);
    }

    /// <summary>
    /// Backups stopped five weeks ago (a disk that failed, unnoticed): retention counts days that have a backup, so the
    /// last 14 are still kept, rather than every backup removed once it is 14 days old.
    /// </summary>
    [Fact]
    public void Backups_that_stopped_keep_their_newest_days()
    {
        var nightly = Nightly(Ist(2026, 9, 1, 1, 30), Ist(2026, 10, 10, 1, 30));
        var settings = new BackupRetentionOptions { Daily = 14, Weekly = 0, Monthly = 12 };

        var kept = Dates(nightly.Except(BackupRetention.ToRemove(nightly, settings, HospitalTimeZone.Zone, Now)));

        kept.ShouldBe([.. Enumerable.Range(0, 14).Select(i => new DateOnly(2026, 9, 27).AddDays(i))]);
    }

    /// <summary>
    /// Nothing is kept past the longest period the privacy notice gives, however the counts fall, except the newest full
    /// backup, which is never removed: without it, nothing would restore.
    /// </summary>
    [Fact]
    public void Nothing_is_kept_past_the_longest_period_but_the_newest_full_backup()
    {
        var settings = new BackupRetentionOptions { Daily = 3, Weekly = 0, Monthly = 0 };
        var old = new[] { Backup(Ist(2026, 10, 1, 1, 30)), Backup(Ist(2026, 10, 2, 1, 30)), Backup(Ist(2026, 10, 2, 9, 0), "premigration") };

        BackupRetention.ToRemove(old, settings, HospitalTimeZone.Zone, Now).ShouldBe([old[2], old[0]], ignoreOrder: true);
    }

    [Theory]
    [InlineData(14, 8, 12, 342)]
    [InlineData(30, 0, 0, 30)]
    [InlineData(7, 4, 0, 22)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(400, 8, 12, 400)]
    public void The_longest_a_backup_is_kept_follows_the_counts(int daily, int weekly, int monthly, int days) =>
        new BackupRetentionOptions { Daily = daily, Weekly = weekly, Monthly = monthly }.LongestDays.ShouldBe(days);

    /// <summary>
    /// With nightly backups, the longest period never removes a backup the counts keep, on any day of two years: the
    /// oldest monthly backup goes once the month after the newest has its first, however long the months between were.
    /// </summary>
    [Fact]
    public void The_longest_period_never_removes_a_backup_the_counts_keep_with_nightly_backups()
    {
        var nightly = Nightly(Ist(2024, 1, 1, 1, 30), Ist(2026, 12, 31, 1, 30));
        for (var day = Ist(2025, 1, 1, 2, 0); day < Ist(2026, 12, 31, 2, 0); day = day.AddDays(1))
        {
            var made = nightly.Where(b => b.StartedAt <= day).ToList();

            // A "now" ten years back puts the longest period's start before every backup: the counts alone decide.
            var byCounts = BackupRetention.ToRemove(made, Defaults, HospitalTimeZone.Zone, day.AddYears(-10)).ToHashSet();
            var withPeriod = BackupRetention.ToRemove(made, Defaults, HospitalTimeZone.Zone, day);

            withPeriod.ToHashSet().SetEquals(byCounts).ShouldBeTrue($"on {day:yyyy-MM-dd} the longest period removed a backup the counts keep");
        }
    }

    /// <summary>
    /// On a directory: the backups past their time and an unfinished file over a day old are removed; the newest backup, a
    /// file HC360 did not name, and an unfinished file still being written are left.
    /// </summary>
    [Fact]
    public void Retention_on_a_directory_removes_only_backups_past_their_time_and_old_unfinished_files()
    {
        Directory.CreateDirectory(_root);
        var nightly = Nightly(Ist(2026, 8, 1, 1, 30), Ist(2026, 11, 14, 1, 30));
        foreach (var backup in nightly)
        {
            File.WriteAllText(InRoot(backup), backup.Path);
        }

        var foreign = Path.Combine(_root, "copied-by-hand.hc360bak");
        File.WriteAllText(foreign, "kept");
        var oldPartial = Path.Combine(_root, "hc360-20261110-200000-scheduled.hc360bak.partial");
        var newPartial = Path.Combine(_root, "hc360-20261113-200000-scheduled.hc360bak.partial");
        File.WriteAllText(oldPartial, "cut off");
        File.WriteAllText(newPartial, "being written");
        File.SetLastWriteTimeUtc(oldPartial, Now.UtcDateTime.AddDays(-2));
        File.SetLastWriteTimeUtc(newPartial, Now.UtcDateTime.AddHours(-1));

        var removed = BackupRetention.Apply(_root, Defaults, HospitalTimeZone.Zone, Now, NullLogger.Instance);

        var expected = BackupRetention.ToRemove(nightly, Defaults, HospitalTimeZone.Zone, Now).Select(InRoot).Append(oldPartial).ToList();
        removed.ShouldBe(expected, ignoreOrder: true);
        removed.Count.ShouldBeGreaterThan(50);
        Directory.EnumerateFiles(_root, "*.hc360bak").Count().ShouldBe(nightly.Count - expected.Count + 2, "the backups kept, and the foreign file");
        File.Exists(foreign).ShouldBeTrue("a file HC360 did not name is left alone");
        File.Exists(newPartial).ShouldBeTrue();
        File.Exists(InRoot(nightly[^1])).ShouldBeTrue();
    }

    [Fact]
    public void A_backup_file_is_known_by_its_name()
    {
        var path = Path.Combine("HC360-Backups", "hc360-20261113-200000-scheduled.hc360bak");
        BackupFile.From(path).ShouldBe(new BackupFile(path, new DateTimeOffset(2026, 11, 13, 20, 0, 0, TimeSpan.Zero), Full: true));
        BackupFile.From("hc360-20261113-083000-premigration.hc360bak").ShouldNotBeNull().Full.ShouldBeFalse();
        BackupFile.From("hc360-20261113-083000-manual.hc360bak").ShouldNotBeNull().Full.ShouldBeTrue();
        BackupFile.From("hc360-20261313-083000-manual.hc360bak").ShouldBeNull("not a date");
        BackupFile.From("hc360-20261113-083000-scheduled.hc360bak.partial").ShouldBeNull("not finished");
        BackupFile.From("backup-before-the-move.hc360bak").ShouldBeNull();
    }

    /// <summary>
    /// What the privacy notice and the erasure answer say of erased data in backups (DPDP Act s8(7)) counts the longest a
    /// backup is kept on the server and its second copy, and the off-site copies too while they are on: locked for 35
    /// days, and removed by the bucket's lifecycle the day after.
    /// </summary>
    [Fact]
    public async Task How_long_backups_are_kept_counts_the_longest_copy()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = Substitute.For<ISettingsService>();
        var options = new PlatformOptions();
        (await BackupRetention.DaysAsync(settings, options, ct)).ShouldBe(342, "a year of monthly backups");

        var offsite = new BackupOffsiteSettings { Enabled = true, RetainDays = 35 };
        settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(offsite);
        (await BackupRetention.DaysAsync(settings, options, ct)).ShouldBe(342);
        options.Backup.Retention = new BackupRetentionOptions { Daily = 14, Weekly = 0, Monthly = 0 };
        (await BackupRetention.DaysAsync(settings, options, ct)).ShouldBe(36, "the off-site lock, and the day after");

        settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(offsite with { Enabled = false });
        (await BackupRetention.DaysAsync(settings, options, ct)).ShouldBe(14);
        settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(offsite with { RetainDays = 0 });
        (await BackupRetention.DaysAsync(settings, options, ct)).ShouldBe(14);
    }

    private string InRoot(BackupFile backup) => Path.Combine(_root, Path.GetFileName(backup.Path));

    private static List<BackupFile> Nightly(DateTimeOffset first, DateTimeOffset last)
    {
        var backups = new List<BackupFile>();
        for (var at = first; at <= last; at = at.AddDays(1))
        {
            backups.Add(Backup(at));
        }

        return backups;
    }

    private static BackupFile Backup(DateTimeOffset at, string kind = "scheduled") =>
        BackupFile.From(string.Create(CultureInfo.InvariantCulture, $"hc360-{at.UtcDateTime:yyyyMMdd-HHmmss}-{kind}.hc360bak"))!;

    private static List<DateOnly> Dates(IEnumerable<BackupFile> backups) =>
        [.. backups.Select(b => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(b.StartedAt, HospitalTimeZone.Zone).DateTime)).Order()];

    private static DateTimeOffset Ist(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, TimeSpan.FromHours(5.5));
}
