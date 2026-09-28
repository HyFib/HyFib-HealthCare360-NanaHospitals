using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Scheduling.Tests.TestSchedules;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

public sealed class BookingRulesTests
{
    private static Slot SlotAt(int hour, int minute, int capacity, int booked) =>
        new(Ist(Monday, hour, minute), Ist(Monday, hour, minute).AddMinutes(15), Monday, new TimeOnly(hour, minute), new TimeOnly(hour, minute + 15),
            capacity, booked, ConsultationMode.InPerson, Facility, Department, "OPD-01", null);

    [Fact]
    public void A_time_that_is_not_a_slot_cannot_be_booked()
    {
        var result = BookingRules.EnsureSlotCanTake(null, overbook: false);

        result.Error!.Code.ShouldBe("appointment.slot_unavailable");
        result.Error.Type.ShouldBe(ErrorType.BusinessRule);
    }

    [Fact]
    public void A_full_slot_is_a_conflict_unless_staff_overbook()
    {
        var full = SlotAt(9, 0, capacity: 1, booked: 1);

        var blocked = BookingRules.EnsureSlotCanTake(full, overbook: false);

        blocked.Error!.Code.ShouldBe("appointment.slot_full");
        blocked.Error.Type.ShouldBe(ErrorType.Conflict);
        BookingRules.EnsureSlotCanTake(full, overbook: true).IsSuccess.ShouldBeTrue();
        BookingRules.EnsureSlotCanTake(SlotAt(9, 0, capacity: 2, booked: 1), overbook: false).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(new int[0], 1, false, 1)]
    [InlineData(new[] { 1 }, 2, false, 2)]
    [InlineData(new[] { 2 }, 2, false, 1)]
    [InlineData(new[] { 1, 2 }, 2, true, 3)]
    [InlineData(new[] { 1 }, 1, true, 2)]
    [InlineData(new[] { 1, 3 }, 3, false, 2)]
    public void Next_seat_is_the_lowest_free_seat(int[] taken, int capacity, bool overbook, int expected) =>
        BookingRules.NextSeat(taken, capacity, overbook).ShouldBe(expected);

    [Fact]
    public void No_seat_is_returned_when_every_seat_is_taken() =>
        BookingRules.NextSeat([1, 2], 2, overbook: false).ShouldBeNull();

    [Fact]
    public void Seat_search_stays_within_the_capacity()
    {
        // Capacity was reduced from 3 to 2 after seats 2 and 3 were booked: seat 1 is still a valid seat,
        // but once seats 1 and 2 are taken nothing above the capacity is handed out without overbooking.
        BookingRules.NextSeat([2, 3], 2, overbook: false).ShouldBe(1);
        BookingRules.NextSeat([1, 2, 3], 2, overbook: false).ShouldBeNull();
        BookingRules.NextSeat([1, 2, 3], 2, overbook: true).ShouldBe(4);
    }

    [Fact]
    public void A_patient_cannot_hold_overlapping_appointments()
    {
        var existing = new PatientBooking(Guid.CreateVersion7(), "NHAP260000042", Ist(Monday, 9, 0), Ist(Monday, 9, 15));

        var clash = BookingRules.EnsureNoOverlap([existing], Ist(Monday, 9, 0), Ist(Monday, 9, 15));

        clash.Error!.Code.ShouldBe("appointment.patient_overlap");
        clash.Error.Type.ShouldBe(ErrorType.Conflict);
        clash.Error.Message.ShouldContain("NHAP260000042");
    }

    [Fact]
    public void Back_to_back_appointments_do_not_overlap()
    {
        var existing = new PatientBooking(Guid.CreateVersion7(), "NHAP260000042", Ist(Monday, 9, 0), Ist(Monday, 9, 15));

        BookingRules.EnsureNoOverlap([existing], Ist(Monday, 9, 15), Ist(Monday, 9, 30)).IsSuccess.ShouldBeTrue();
        BookingRules.EnsureNoOverlap([existing], Ist(Monday, 8, 45), Ist(Monday, 9, 0)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Online_changes_close_at_the_cutoff()
    {
        var startsAt = Ist(Monday, 10, 0);
        var cutoff = TimeSpan.FromMinutes(30);

        BookingRules.EnsureOnlineChangeAllowed(startsAt, startsAt.AddMinutes(-30), cutoff).IsSuccess.ShouldBeTrue();
        BookingRules.EnsureOnlineChangeAllowed(startsAt, startsAt.AddMinutes(-29), cutoff).Error!.Code.ShouldBe("appointment.online_cutoff");
    }

    [Fact]
    public void Online_booking_is_limited_to_the_booking_window()
    {
        var now = Ist(Monday, 8, 0);
        var policy = new OnlineBookingPolicy(TimeSpan.FromMinutes(30), 30);

        BookingRules.EnsureOnlineBookable(now.AddDays(29), now, policy).IsSuccess.ShouldBeTrue();
        BookingRules.EnsureOnlineBookable(now.AddDays(31), now, policy).Error!.Code.ShouldBe("appointment.too_far_ahead");
        BookingRules.EnsureOnlineBookable(now.AddMinutes(10), now, policy).Error!.Code.ShouldBe("appointment.online_cutoff");
    }

    [Fact]
    public void Follow_up_visits_use_the_follow_up_fee()
    {
        BookingRules.FeeFor(AppointmentVisitType.New, 600m, 360m).ShouldBe(600m);
        BookingRules.FeeFor(AppointmentVisitType.FollowUp, 600m, 360m).ShouldBe(360m);
        BookingRules.FeeFor(AppointmentVisitType.New, 499.995m, 0m).ShouldBe(500.00m);
    }
}
