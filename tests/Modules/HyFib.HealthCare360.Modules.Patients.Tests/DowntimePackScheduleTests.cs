using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// When the downtime pack is made: nightly after 02:00 (hospital time), tried again if it failed instead of being marked
/// done, and refreshed through the day so the ward sheets and medicines in the offline copy are never a day old.
/// </summary>
public sealed class DowntimePackScheduleTests
{
    private static readonly Guid Facility = Guid.NewGuid();

    // 14 Nov 2026, hospital time (IST, UTC+5:30).
    private static DateTimeOffset At(int hour, int minute = 0, int day = 14) =>
        new DateTimeOffset(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();

    [Fact]
    public void The_nightly_pack_waits_for_two_in_the_morning_but_refreshing_goes_on_through_the_night()
    {
        var schedule = new DowntimePackSchedule();

        schedule.Due(Facility, At(1, 59), 0).ShouldBeNull("no nightly pack before two, and refresh 0 means the nightly pack only");
        schedule.Due(Facility, At(0, 30), 30).ShouldBe(DowntimePackKind.Refreshed, "the wards give medicines all night");
        schedule.Made(Facility, DowntimePackKind.Refreshed, At(0, 30));

        schedule.Due(Facility, At(0, 59), 30).ShouldBeNull();
        schedule.Due(Facility, At(1, 0), 30).ShouldBe(DowntimePackKind.Refreshed);
        schedule.Made(Facility, DowntimePackKind.Refreshed, At(1, 59));

        schedule.Due(Facility, At(2, 0), 30).ShouldBe(DowntimePackKind.Nightly, "the nightly pack is still made after two");
    }

    [Fact]
    public void The_nightly_pack_is_due_after_two_and_done_once_made()
    {
        var schedule = new DowntimePackSchedule();

        schedule.Due(Facility, At(2, 5), 0).ShouldBe(DowntimePackKind.Nightly);
        schedule.Made(Facility, DowntimePackKind.Nightly, At(2, 5));

        schedule.Due(Facility, At(14, 30), 0).ShouldBeNull("refresh 0 means the nightly pack only");
    }

    [Fact]
    public void A_failed_nightly_pack_is_not_marked_done_and_is_tried_again_after_a_short_wait()
    {
        var schedule = new DowntimePackSchedule();
        schedule.Due(Facility, At(2, 5), 30).ShouldBe(DowntimePackKind.Nightly);
        schedule.Failed(Facility, At(2, 5));

        schedule.Due(Facility, At(2, 10), 30).ShouldBeNull("not on every tick");
        schedule.Due(Facility, At(2, 5).Add(DowntimePackSchedule.RetryAfter), 30).ShouldBe(DowntimePackKind.Nightly, "yesterday's pack is not left in place");
        schedule.Due(Facility, At(9, 0), 0).ShouldBe(DowntimePackKind.Nightly, "still not done hours later");
    }

    [Fact]
    public void A_pack_made_after_a_failure_clears_the_wait()
    {
        var schedule = new DowntimePackSchedule();
        schedule.Failed(Facility, At(2, 5));
        schedule.Made(Facility, DowntimePackKind.Nightly, At(2, 20));

        schedule.Due(Facility, At(2, 21), 0).ShouldBeNull();
        schedule.Due(Facility, At(2, 21), 30).ShouldBeNull("the nightly pack just made is current");
    }

    [Fact]
    public void A_refreshed_pack_is_due_each_period_after_the_last_one_made()
    {
        var schedule = new DowntimePackSchedule();
        schedule.Made(Facility, DowntimePackKind.Nightly, At(2, 5));

        schedule.Due(Facility, At(2, 34), 30).ShouldBeNull();
        schedule.Due(Facility, At(2, 35), 30).ShouldBe(DowntimePackKind.Refreshed);
        schedule.Made(Facility, DowntimePackKind.Refreshed, At(2, 35));

        schedule.Due(Facility, At(3, 0), 30).ShouldBeNull();
        schedule.Due(Facility, At(3, 5), 30).ShouldBe(DowntimePackKind.Refreshed);
    }

    [Fact]
    public void A_refreshed_pack_does_not_stand_in_for_the_nightly_one()
    {
        var schedule = new DowntimePackSchedule();
        schedule.Made(Facility, DowntimePackKind.Refreshed, At(2, 5));

        schedule.Due(Facility, At(2, 6), 30).ShouldBe(DowntimePackKind.Nightly);
    }

    [Fact]
    public void Each_new_day_starts_with_the_nightly_pack_again()
    {
        var schedule = new DowntimePackSchedule();
        schedule.Made(Facility, DowntimePackKind.Nightly, At(2, 5));

        schedule.Due(Facility, At(23, 50), 0).ShouldBeNull();
        schedule.Due(Facility, At(2, 0, day: 15), 0).ShouldBe(DowntimePackKind.Nightly);
    }

    [Fact]
    public void One_facility_failing_does_not_hold_back_another()
    {
        var schedule = new DowntimePackSchedule();
        var other = Guid.NewGuid();
        schedule.Failed(Facility, At(2, 5));

        schedule.Due(other, At(2, 6), 30).ShouldBe(DowntimePackKind.Nightly);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 0)]
    [InlineData(15, 15)]
    [InlineData(-5, 0)]
    [InlineData(100000, 1440)]
    public void The_refresh_setting_defaults_to_thirty_minutes_and_stays_in_bounds(int? setting, int expected) =>
        DowntimePackSchedule.RefreshMinutes(setting).ShouldBe(expected);

    [Fact]
    public void The_refreshed_packs_of_the_day_are_kept_for_hours_not_days()
    {
        DowntimePackSnapshot.RefreshedKeptFor.ShouldBeLessThan(DowntimePackSnapshot.KeptFor);
        DowntimePackSnapshot.RefreshedKeptFor.ShouldBeLessThanOrEqualTo(TimeSpan.FromDays(1));
    }
}
