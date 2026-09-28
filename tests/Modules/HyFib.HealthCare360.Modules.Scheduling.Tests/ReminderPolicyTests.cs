using HyFib.HealthCare360.Modules.Scheduling.Domain;
using static HyFib.HealthCare360.Modules.Scheduling.Tests.TestSchedules;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

public sealed class ReminderPolicyTests
{
    private static readonly DateTimeOffset Start = Ist(Monday, 10, 0);

    private static ReminderDecision Decide(DateTimeOffset bookedAt, DateTimeOffset now, DateTimeOffset? dayBeforeSent = null, DateTimeOffset? twoHourSent = null) =>
        ReminderPolicy.Decide(AppointmentStatus.Booked, Start, bookedAt, dayBeforeSent, twoHourSent, now);

    [Fact]
    public void Nothing_is_due_more_than_a_day_ahead() =>
        Decide(Start.AddDays(-5), Start.AddHours(-25)).HasWork.ShouldBeFalse();

    [Fact]
    public void The_day_before_reminder_goes_out_24_hours_ahead()
    {
        var decision = Decide(Start.AddDays(-5), Start.AddHours(-23));

        decision.SendDayBefore.ShouldBeTrue();
        decision.SendTwoHour.ShouldBeFalse();
    }

    [Fact]
    public void The_two_hour_reminder_follows_once_the_day_before_one_was_sent()
    {
        var decision = Decide(Start.AddDays(-5), Start.AddMinutes(-110), dayBeforeSent: Start.AddHours(-24));

        decision.SendTwoHour.ShouldBeTrue();
        decision.SendDayBefore.ShouldBeFalse();
        decision.SkipDayBefore.ShouldBeFalse();
    }

    [Fact]
    public void A_booking_made_within_24_hours_skips_the_day_before_reminder()
    {
        var decision = Decide(Start.AddHours(-5), Start.AddHours(-4));

        decision.SkipDayBefore.ShouldBeTrue();
        decision.SendsMessage.ShouldBeFalse();
    }

    [Fact]
    public void A_booking_made_within_2_hours_gets_no_reminders()
    {
        var decision = Decide(Start.AddHours(-1), Start.AddMinutes(-30));

        decision.SkipDayBefore.ShouldBeTrue();
        decision.SkipTwoHour.ShouldBeTrue();
        decision.SendsMessage.ShouldBeFalse();
    }

    [Fact]
    public void After_a_job_outage_only_one_reminder_is_sent()
    {
        var decision = Decide(Start.AddDays(-3), Start.AddHours(-1));

        decision.SkipDayBefore.ShouldBeTrue();
        decision.SendDayBefore.ShouldBeFalse();
        decision.SendTwoHour.ShouldBeTrue();
    }

    [Fact]
    public void Reminders_already_sent_are_never_repeated() =>
        Decide(Start.AddDays(-3), Start.AddHours(-1), Start.AddHours(-24), Start.AddHours(-2)).HasWork.ShouldBeFalse();

    [Theory]
    [InlineData(AppointmentStatus.Cancelled)]
    [InlineData(AppointmentStatus.Rescheduled)]
    [InlineData(AppointmentStatus.CheckedIn)]
    [InlineData(AppointmentStatus.NoShow)]
    public void Only_open_appointments_get_reminders(AppointmentStatus status) =>
        ReminderPolicy.Decide(status, Start, Start.AddDays(-3), null, null, Start.AddHours(-1)).HasWork.ShouldBeFalse();

    [Fact]
    public void Nothing_is_sent_once_the_appointment_time_has_passed() =>
        Decide(Start.AddDays(-3), Start.AddMinutes(1)).HasWork.ShouldBeFalse();

    [Fact]
    public void Confirmed_appointments_are_reminded_too() =>
        ReminderPolicy.Decide(AppointmentStatus.Confirmed, Start, Start.AddDays(-3), null, null, Start.AddHours(-20)).SendDayBefore.ShouldBeTrue();
}
