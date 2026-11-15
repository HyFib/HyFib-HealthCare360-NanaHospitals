using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The nightly backup the server was off for is taken when it starts again: the job scheduler keeps its schedule in
/// memory, so a run missed while the server was stopped would otherwise never happen, and the hospital would go a day
/// (or more) without a backup it believes it has. It is taken in the backup window, when the hospital is quiet, and in the
/// day only once the newest good backup is stale, and then gently.
/// </summary>
public sealed class BackupCatchUpTests
{
    private const string Nightly = "0 0 2 * * ?";
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    // 02:00 IST is 20:30 UTC the evening before.
    private static DateTimeOffset Ist(int day, int hour, int minute = 0) => new DateTimeOffset(2026, 10, day, hour, minute, 0, TimeSpan.FromMinutes(330));

    [Fact]
    public void A_nightly_run_the_server_was_off_for_is_due()
    {
        // Last backup the night of the 26th; off from 23:00 on the 26th to 08:00 on the 27th.
        BackupCatchUpJob.Missed(Nightly, India, lastAttempt: Ist(26, 2, 4), now: Ist(27, 8)).ShouldBeTrue();
    }

    [Fact]
    public void Nothing_is_due_when_the_last_run_was_after_the_last_scheduled_time()
    {
        BackupCatchUpJob.Missed(Nightly, India, lastAttempt: Ist(27, 2, 1), now: Ist(27, 8)).ShouldBeFalse();
        BackupCatchUpJob.Missed(Nightly, India, lastAttempt: Ist(26, 2, 1), now: Ist(27, 1, 59)).ShouldBeFalse("the next run is still to come");
    }

    [Fact]
    public void A_new_installation_waits_for_its_schedule() =>
        BackupCatchUpJob.Missed(Nightly, India, lastAttempt: null, now: Ist(27, 8)).ShouldBeFalse();

    /// <summary>
    /// The check is on the clock, every quarter of an hour: one that comes a few minutes after the night's backup was due
    /// leaves it to the nightly job, which starts on the minute, rather than take it a second time beside it.
    /// </summary>
    [Fact]
    public void A_backup_only_minutes_late_is_not_missed_yet()
    {
        BackupCatchUpJob.Missed(Nightly, India, lastAttempt: Ist(26, 2, 4), now: Ist(27, 2, 7)).ShouldBeFalse();
        BackupCatchUpJob.Missed(Nightly, India, lastAttempt: Ist(26, 2, 4), now: Ist(27, 2, 10)).ShouldBeTrue();
    }

    /// <summary>A power cut over the night's backup, the server back at 03:00: still the backup window, so it is taken at once.</summary>
    [Fact]
    public void A_missed_backup_is_taken_at_once_inside_the_backup_window() =>
        BackupCatchUpJob.Decide(Nightly, India, lastAttempt: Ist(26, 2, 0), lastGood: Ist(26, 2, 40), now: Ist(27, 3, 0))
            .ShouldBe(BackupCatchUp.Now);

    /// <summary>
    /// Back at 08:00, the hospital at work: the newest good backup is a day old, so tonight's backup takes it, rather than
    /// a full backup loading the server through the morning's outpatients.
    /// </summary>
    [Theory]
    [InlineData(6, 1)]
    [InlineData(8, 0)]
    [InlineData(14, 0)]
    public void Outside_the_window_a_missed_backup_waits_for_the_next_one_while_the_newest_is_fresh(int hour, int minute) =>
        BackupCatchUpJob.Decide(Nightly, India, lastAttempt: Ist(26, 2, 0), lastGood: Ist(26, 2, 40), now: Ist(27, hour, minute))
            .ShouldBe(BackupCatchUp.None);

    /// <summary>
    /// Once the newest good backup is older than System health allows (36 hours, <c>backup.stale</c>), the missed backup
    /// is taken at once even in the day, gently: one pg_dump, below normal priority.
    /// </summary>
    [Fact]
    public void A_missed_backup_is_taken_gently_in_the_day_once_the_newest_is_stale()
    {
        BackupCatchUpJob.Decide(Nightly, India, lastAttempt: Ist(26, 2, 0), lastGood: Ist(26, 2, 40), now: Ist(27, 14, 40))
            .ShouldBe(BackupCatchUp.None, "exactly 36 hours old: not stale yet");
        BackupCatchUpJob.Decide(Nightly, India, lastAttempt: Ist(26, 2, 0), lastGood: Ist(26, 2, 40), now: Ist(27, 14, 52))
            .ShouldBe(BackupCatchUp.NowGently);

        // Off for two nights: the newest good backup is two days old when the server is back in the morning.
        BackupCatchUpJob.Decide(Nightly, India, lastAttempt: Ist(25, 2, 0), lastGood: Ist(25, 2, 40), now: Ist(27, 9, 0))
            .ShouldBe(BackupCatchUp.NowGently);
    }

    [Fact]
    public void A_missed_backup_with_no_good_backup_at_all_is_taken_gently_at_once() =>
        BackupCatchUpJob.Decide(Nightly, India, lastAttempt: Ist(26, 2, 0), lastGood: null, now: Ist(27, 9, 0))
            .ShouldBe(BackupCatchUp.NowGently);

    [Fact]
    public void Nothing_is_taken_when_nothing_was_missed()
    {
        BackupCatchUpJob.Decide(Nightly, India, lastAttempt: Ist(27, 2, 0), lastGood: Ist(27, 2, 40), now: Ist(27, 3, 0)).ShouldBe(BackupCatchUp.None);
        BackupCatchUpJob.Decide(Nightly, India, lastAttempt: null, lastGood: null, now: Ist(27, 3, 0)).ShouldBe(BackupCatchUp.None, "a new installation");
    }
}
