using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// System health on the copies outside the building (<c>backup.offsite_stale</c>), over the week of a hospital whose
/// nightly backup is made at 01:30 and uploaded to its bucket: nothing while the newest backup goes up, a warning with the
/// store's reason while its upload fails, and critical, so IT on-call's phone is told, once no backup has reached the
/// bucket for three and a half days or the newest one has waited a day.
/// </summary>
public sealed class BackupOffsiteWarningTests
{
    private const string Store = "bucket nana-hc360-backups at s3.ap-south-1.amazonaws.com";
    private const string NoInternet = "The store could not be reached: No such host is known. (s3.ap-south-1.amazonaws.com:443)";

    private static readonly DateTimeOffset FridayBackup = Ist(6, 1, 30);

    [Fact]
    public void A_backup_that_is_off_site_raises_nothing()
    {
        BackupOffsite.Warning(Store, History(FridayBackup, uploaded: true, lastOffsite: FridayBackup), Ist(6, 9, 0), HospitalTimeZone.Zone).ShouldBeNull();
    }

    /// <summary>The night's backup waits for the next run, or is going up: nothing to say yet.</summary>
    [Fact]
    public void A_backup_waiting_for_or_in_its_first_upload_raises_nothing_within_the_day()
    {
        var tonight = Ist(7, 1, 30);
        BackupOffsite.Warning(Store, History(tonight, lastOffsite: FridayBackup), Ist(7, 1, 50), HospitalTimeZone.Zone).ShouldBeNull();
        BackupOffsite.Warning(Store, History(tonight, lastOffsite: FridayBackup, progress: (40, 900)), Ist(7, 6, 0), HospitalTimeZone.Zone).ShouldBeNull();
    }

    /// <summary>
    /// The upload fails: a warning at once, with the store's answer. The internet down from Friday's backup on: critical on
    /// the Monday afternoon, three and a half days after the newest backup that reached the bucket.
    /// </summary>
    [Theory]
    [InlineData(7, 9, 0, "warning")]
    [InlineData(9, 9, 0, "warning")]
    [InlineData(9, 13, 29, "warning")]
    [InlineData(9, 13, 31, "critical")]
    [InlineData(10, 9, 0, "critical")]
    public void Uploads_that_fail_warn_and_after_three_and_a_half_days_are_critical(int day, int hour, int minute, string severity)
    {
        var lastNight = Ist(day, 1, 30);
        var warning = BackupOffsite.Warning(Store, History(lastNight, error: NoInternet, lastOffsite: FridayBackup, firstFailed: Ist(7, 1, 30)),
            Ist(day, hour, minute), HospitalTimeZone.Zone).ShouldNotBeNull();

        (warning.Code, warning.Severity).ShouldBe(("backup.offsite_stale", severity));
        warning.Message.ShouldContain(NoInternet);
        warning.Message.ShouldContain(severity == "warning"
            ? $"The backup of {day:00} Nov 2026 01:30 has not reached the off-site copy in {Store} yet"
            : $"No backup has reached the off-site copy in {Store} since the one of 06 Nov 2026 01:30, which is the newest backup outside the building");
    }

    /// <summary>A bucket that never worked (a wrong access key at set-up) is critical once its uploads have failed that long.</summary>
    [Fact]
    public void Uploads_that_never_succeeded_turn_critical_after_failing_three_and_a_half_days()
    {
        var history = History(Ist(9, 1, 30), error: "The store answered 403 AccessDenied: Access Denied", firstFailed: FridayBackup);

        BackupOffsite.Warning(Store, history, Ist(9, 9, 0), HospitalTimeZone.Zone).ShouldNotBeNull().Severity.ShouldBe("warning");
        var critical = BackupOffsite.Warning(Store, history, Ist(9, 13, 31), HospitalTimeZone.Zone).ShouldNotBeNull();
        critical.Severity.ShouldBe("critical");
        critical.Message.ShouldContain($"No backup has ever reached the off-site copy in {Store}");
        critical.Message.ShouldContain("403 AccessDenied");
    }

    /// <summary>
    /// The newest backup still not in the bucket a day after it was checked is critical, whatever else: a slow uplink that
    /// cannot carry a night's backup in a day, or a server that stopped trying (the job node is down).
    /// </summary>
    [Fact]
    public void The_newest_backup_still_not_off_site_after_a_day_is_critical()
    {
        var friday = History(FridayBackup, lastOffsite: Ist(5, 1, 30), progress: (700, 900));
        BackupOffsite.Warning(Store, friday, Ist(7, 1, 29), HospitalTimeZone.Zone).ShouldBeNull();

        var slow = BackupOffsite.Warning(Store, friday, Ist(7, 1, 46), HospitalTimeZone.Zone).ShouldNotBeNull();
        slow.Severity.ShouldBe("critical");
        slow.Message.ShouldContain("is still not in");
        slow.Message.ShouldContain("700 of 900 parts are in the bucket");

        var notTried = BackupOffsite.Warning(Store, History(FridayBackup, lastOffsite: Ist(5, 1, 30)), Ist(7, 2, 0), HospitalTimeZone.Zone).ShouldNotBeNull();
        notTried.Severity.ShouldBe("critical");
        notTried.Message.ShouldContain("HC360 has not tried to upload it");
    }

    /// <summary>On a live server with no off-site copy set up, System health says plainly that nothing leaves the building.</summary>
    [Fact]
    public void No_off_site_copy_set_up_is_a_warning_that_nothing_leaves_the_building()
    {
        var warning = BackupOffsite.NotSetUp(null);

        (warning.Code, warning.Severity).ShouldBe(("backup.offsite_stale", "warning"));
        warning.Message.ShouldStartWith("No copy of the backups leaves the building");
        warning.Message.ShouldContain("Administration › Backups (Off-site copy)");
    }

    /// <summary>Training servers, and servers with nightly backups off, are not told to set one up; a live one is.</summary>
    [Fact]
    public async Task Only_a_live_server_with_nightly_backups_is_told_to_set_one_up()
    {
        var settings = Substitute.For<ISettingsService>();
        var db = (HospitalDbContext)null!;

        var live = await BackupOffsite.WarningAsync(db, settings, new PlatformOptions(), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        live.ShouldNotBeNull().Message.ShouldStartWith("No copy of the backups leaves the building");

        (await BackupOffsite.WarningAsync(db, settings, new PlatformOptions { SeedDemoData = true }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken))
            .ShouldBeNull();
        var nightlyOff = new PlatformOptions();
        nightlyOff.Backup.Enabled = false;
        (await BackupOffsite.WarningAsync(db, settings, nightlyOff, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private static BackupOffsiteHistory History(
        DateTimeOffset latest, bool uploaded = false, string? error = null, DateTimeOffset? lastOffsite = null, DateTimeOffset? firstFailed = null,
        (int, int)? progress = null) =>
        new(latest, latest.AddMinutes(15), uploaded, error, progress, lastOffsite, firstFailed);

    private static DateTimeOffset Ist(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(5.5));
}
