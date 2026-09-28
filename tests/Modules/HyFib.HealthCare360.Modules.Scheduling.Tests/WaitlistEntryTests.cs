using HyFib.HealthCare360.Modules.Scheduling.Domain;
using static HyFib.HealthCare360.Modules.Scheduling.Tests.TestSchedules;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

public sealed class WaitlistEntryTests
{
    private static WaitlistEntry Entry(PreferredSession session = PreferredSession.Any, bool telehealth = false) => new()
    {
        FacilityId = Facility,
        PatientId = Patient,
        PractitionerId = Doctor,
        PreferredFrom = Monday,
        PreferredTo = Monday.AddDays(4),
        PreferredSession = session,
        IsTelehealth = telehealth,
        Status = WaitlistStatus.Waiting,
    };

    [Theory]
    [InlineData(PreferredSession.Morning, 11, 59, true)]
    [InlineData(PreferredSession.Morning, 12, 0, false)]
    [InlineData(PreferredSession.Afternoon, 12, 0, true)]
    [InlineData(PreferredSession.Afternoon, 16, 0, false)]
    [InlineData(PreferredSession.Evening, 16, 0, true)]
    [InlineData(PreferredSession.Evening, 9, 0, false)]
    [InlineData(PreferredSession.Any, 7, 0, true)]
    public void Preferred_sessions_follow_the_ist_clock(PreferredSession session, int hour, int minute, bool expected) =>
        WaitlistEntry.InSession(session, new TimeOnly(hour, minute)).ShouldBe(expected);

    [Fact]
    public void A_request_wants_matching_dates_sessions_and_mode_only()
    {
        var entry = Entry(PreferredSession.Morning);

        entry.Wants(Tuesday, new TimeOnly(10, 0), telehealth: false).ShouldBeTrue();
        entry.Wants(Monday.AddDays(5), new TimeOnly(10, 0), telehealth: false).ShouldBeFalse();
        entry.Wants(Tuesday, new TimeOnly(17, 0), telehealth: false).ShouldBeFalse();
        entry.Wants(Tuesday, new TimeOnly(10, 0), telehealth: true).ShouldBeFalse();
    }

    [Fact]
    public void Offers_keep_the_request_open_and_count_attempts()
    {
        var entry = Entry();

        entry.Offer(Ist(Tuesday, 10, 0), Ist(Monday, 18, 0)).IsSuccess.ShouldBeTrue();
        entry.Offer(Ist(Tuesday, 11, 0), Ist(Monday, 19, 0)).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(WaitlistStatus.Offered);
        entry.OfferCount.ShouldBe(2);
        entry.OfferedSlotStartsAt.ShouldBe(Ist(Tuesday, 11, 0));
        entry.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void Booking_closes_the_request()
    {
        var entry = Entry();
        var appointmentId = Guid.CreateVersion7();

        entry.MarkBooked(appointmentId, Ist(Monday, 18, 0)).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(WaitlistStatus.Booked);
        entry.AppointmentId.ShouldBe(appointmentId);
        entry.Wants(Tuesday, new TimeOnly(10, 0), false).ShouldBeFalse();
        entry.Offer(Ist(Tuesday, 10, 0), Ist(Monday, 19, 0)).Error!.Code.ShouldBe("waitlist.closed");
        entry.Cancel(Ist(Monday, 19, 0)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Requests_expire_after_their_last_preferred_date()
    {
        var entry = Entry();

        entry.ExpireIfPast(Monday.AddDays(4), Ist(Monday, 9, 0)).ShouldBeFalse();
        entry.ExpireIfPast(Monday.AddDays(5), Ist(Monday.AddDays(5), 9, 0)).ShouldBeTrue();
        entry.Status.ShouldBe(WaitlistStatus.Expired);
    }

    [Fact]
    public void Preferred_window_must_be_upcoming_and_bounded()
    {
        WaitlistEntry.ValidateWindow(Monday, Monday.AddDays(10), Monday).IsSuccess.ShouldBeTrue();
        WaitlistEntry.ValidateWindow(Monday.AddDays(-1), Monday, Monday).Error!.Fields!.ShouldContainKey("preferredFrom");
        WaitlistEntry.ValidateWindow(Monday, Monday.AddDays(-1), Monday).Error!.Fields!.ShouldContainKey("preferredTo");
        WaitlistEntry.ValidateWindow(Monday, Monday.AddDays(WaitlistEntry.MaxPreferredDays), Monday).IsFailure.ShouldBeTrue();
    }
}
