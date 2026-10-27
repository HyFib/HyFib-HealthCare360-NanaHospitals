using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The nightly backup the server was off for is taken when it starts again: the job scheduler keeps its schedule in
/// memory, so a run missed while the server was stopped would otherwise never happen, and the hospital would go a day
/// (or more) without a backup it believes it has.
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
}
