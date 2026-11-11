using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// System health on the backups' copies to the secondary directory (DED-16), over the week of a hospital that swaps two
/// USB disks: the copy made on Friday at 01:30, the disk taken out that day for the swap and the other never plugged in.
/// The integration tests check the same over real backups, the API and the alerts (SystemHealthAlertTests there).
/// </summary>
public sealed class BackupCopyWarningTests
{
    private const string Disk = @"E:\HC360-Backups";
    private const string Unplugged = BackupService.CopyFailedNote + @"Could not find a part of the path 'E:\HC360-Backups'.";

    private static readonly DateTimeOffset FridayCopy = Ist(6, 1, 30);

    [Fact]
    public void A_backup_that_was_copied_raises_nothing()
    {
        BackupCopies.Warning(Disk, new(FridayCopy, LatestCopied: true, null, FridayCopy, null), Ist(6, 9, 0), HospitalTimeZone.Zone).ShouldBeNull();
    }

    /// <summary>
    /// The first night without a copy is a warning, and says what was not copied, where to, why and what to do. Three nights
    /// without one, it is critical: told to IT on-call on the Monday afternoon, clear of Monday night's backup.
    /// </summary>
    [Theory]
    [InlineData(7, 9, 0, "warning")]
    [InlineData(9, 9, 0, "warning")]
    [InlineData(9, 13, 29, "warning")]
    [InlineData(9, 13, 31, "critical")]
    [InlineData(10, 9, 0, "critical")]
    public void Nights_without_a_copy_warn_and_after_three_and_a_half_days_are_critical(int day, int hour, int minute, string severity)
    {
        var now = Ist(day, hour, minute);
        var lastNight = Ist(day, 1, 30);

        var warning = BackupCopies.Warning(Disk, new(lastNight, LatestCopied: false, Unplugged, FridayCopy, Ist(7, 1, 30)), now, HospitalTimeZone.Zone)
            .ShouldNotBeNull();

        (warning.Code, warning.Severity).ShouldBe(("backup.copy_failed", severity));
        warning.Message.ShouldContain(@"Could not find a part of the path 'E:\HC360-Backups'.");
        warning.Message.ShouldContain("Back up now");
        warning.Message.ShouldContain(severity == "warning"
            ? $@"The backup of {day:00} Nov 2026 01:30 was not copied to E:\HC360-Backups, so it is on the server's own disk only"
            : @"No backup has been copied to E:\HC360-Backups since the one of 06 Nov 2026 01:30, which is the newest backup off the server's own disk");
    }

    /// <summary>A disk that never worked (a wrong drive letter at installation) is critical once its copies have failed that long.</summary>
    [Fact]
    public void Copies_that_never_succeeded_turn_critical_after_failing_three_and_a_half_days()
    {
        var history = new BackupCopyHistory(Ist(9, 1, 30), LatestCopied: false, Unplugged, LastCopiedAt: null, FirstFailedCopyAt: FridayCopy);

        BackupCopies.Warning(Disk, history, Ist(7, 9, 0), HospitalTimeZone.Zone).ShouldNotBeNull().Severity.ShouldBe("warning");
        var critical = BackupCopies.Warning(Disk, history, Ist(9, 13, 31), HospitalTimeZone.Zone).ShouldNotBeNull();
        critical.Severity.ShouldBe("critical");
        critical.Message.ShouldContain(@"No backup has ever been copied to E:\HC360-Backups, and every copy since 06 Nov 2026 01:30 failed");
    }

    /// <summary>
    /// Backups made before the secondary directory was set up were never copied, and none failed: a warning, however old
    /// they are, rather than an alert the moment the disks arrive. The next backup is copied, or its copy fails.
    /// </summary>
    [Fact]
    public void A_backup_made_before_the_copies_were_set_up_is_a_warning_however_old()
    {
        var history = new BackupCopyHistory(Ist(1, 1, 30), LatestCopied: false, null, LastCopiedAt: null, FirstFailedCopyAt: null);

        var warning = BackupCopies.Warning(Disk, history, Ist(30, 9, 0), HospitalTimeZone.Zone).ShouldNotBeNull();

        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldContain(@"The latest backup, of 01 Nov 2026 01:30, has no copy in E:\HC360-Backups: none was attempted");
    }

    [Fact]
    public void Why_the_copy_failed_is_read_after_any_other_note_on_the_backup()
    {
        BackupCopies.Reason("Checked without pg_restore: the database dump was not listed. " + BackupService.CopyFailedNote + "The network path was not found")
            .ShouldBe("The network path was not found.");
        BackupCopies.Reason("Checked without pg_restore: the database dump was not listed.").ShouldBeNull();
        BackupCopies.Reason(null).ShouldBeNull();
    }

    [Fact]
    public void The_administrators_are_told_which_backup_was_not_copied_where_and_why()
    {
        BackupCopies.NotCopiedBody("hc360-20261107-013000-scheduled.hc360bak", Disk, Unplugged).ShouldBe(
            @"hc360-20261107-013000-scheduled.hc360bak was made, but not copied to E:\HC360-Backups, so it is on the server's own disk only. " +
            @"Could not find a part of the path 'E:\HC360-Backups'. " +
            "Connect the backup disk, or check the NAS share, then take a backup under Administration › Backups (Back up now).");
    }

    private static DateTimeOffset Ist(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(5.5));
}
