using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

public sealed class SurgeryBookingTests
{
    private static readonly Guid Theatre = Guid.CreateVersion7();

    [Fact]
    public void Scheduling_needs_an_operating_surgeon()
    {
        var booking = TestData.NewBooking();
        booking.SurgeonId = null;

        booking.Schedule(Theatre, TestData.Now.AddHours(1), TestData.Now.AddHours(2), TestData.Now).Error!.Fields!.ShouldContainKey("surgeonId");
    }

    [Theory]
    [InlineData(AnaesthesiaType.GA)]
    [InlineData(AnaesthesiaType.Spinal)]
    [InlineData(AnaesthesiaType.Sedation)]
    public void Scheduling_needs_an_anaesthetist_except_for_local_anaesthesia(AnaesthesiaType type)
    {
        var booking = TestData.NewBooking(type);
        booking.AnaesthetistId = null;

        booking.Schedule(Theatre, TestData.Now.AddHours(1), TestData.Now.AddHours(2), TestData.Now).Error!.Fields!.ShouldContainKey("anaesthetistId");
    }

    [Fact]
    public void Local_anaesthesia_case_can_be_scheduled_without_anaesthetist()
    {
        var booking = TestData.NewBooking(AnaesthesiaType.Local);

        booking.Schedule(Theatre, TestData.Now.AddHours(1), TestData.Now.AddHours(2), TestData.Now).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(SurgeryStatus.Scheduled);
        booking.ToSlot().ShouldNotBeNull().TheatreId.ShouldBe(Theatre);
    }

    [Fact]
    public void Emergency_bookings_may_be_entered_up_to_an_hour_late_but_not_more()
    {
        TestData.NewBooking().Schedule(Theatre, TestData.Now.AddMinutes(-50), TestData.Now.AddHours(1), TestData.Now).IsSuccess.ShouldBeTrue();
        TestData.NewBooking().Schedule(Theatre, TestData.Now.AddHours(-2), TestData.Now.AddHours(1), TestData.Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Postponed_booking_can_be_rescheduled()
    {
        var booking = TestData.Scheduled();
        booking.Postpone("Patient febrile").IsSuccess.ShouldBeTrue();

        booking.Schedule(Theatre, TestData.Now.AddDays(2), TestData.Now.AddDays(2).AddHours(2), TestData.Now).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(SurgeryStatus.Scheduled);
        booking.PostponeCount.ShouldBe(1);
    }

    [Fact]
    public void Started_surgery_cannot_be_rescheduled_cancelled_or_postponed()
    {
        var booking = TestData.InProgress();

        booking.Schedule(Theatre, TestData.Now.AddDays(1), TestData.Now.AddDays(1).AddHours(1), TestData.Now).IsFailure.ShouldBeTrue();
        booking.Cancel("Changed mind").IsFailure.ShouldBeTrue();
        booking.Postpone("Changed mind").IsFailure.ShouldBeTrue();
        booking.CanChangePlan().IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Cancelled_booking_is_closed()
    {
        var booking = TestData.Scheduled();

        booking.Cancel("Patient withdrew consent").IsSuccess.ShouldBeTrue();

        booking.IsClosed.ShouldBeTrue();
        booking.CancelReason.ShouldBe("Patient withdrew consent");
        booking.ToSlot().ShouldNotBeNull("the old slot is kept for the record, but a cancelled booking no longer blocks it");
        SurgeryScheduling.IsBlocking(booking.Status).ShouldBeFalse();
    }

    [Fact]
    public void Receiving_in_pre_op_needs_a_scheduled_booking() =>
        TestData.NewBooking().ReceiveInPreOp(TestData.Now).Error!.Code.ShouldBe("surgery.not_scheduled");

    [Fact]
    public void Pre_op_checklist_starts_with_every_item_pending()
    {
        var booking = TestData.NewBooking();

        booking.PreOpChecks.Count.ShouldBe(PreOpChecklist.Items.Count);
        PreOpChecklist.IsComplete(booking.PreOpChecks).ShouldBeFalse();
        booking.SafetyChecks.Count.ShouldBe(SurgicalSafetyChecklist.Phases.Values.Sum(p => p.Count));
    }

    [Fact]
    public void Consent_can_never_be_marked_not_applicable() =>
        TestData.Scheduled().RecordPreOp([new ChecklistInput("consent", ChecklistItemState.NotApplicable, "Unconscious patient")], TestData.Staff())
            .Error!.Code.ShouldBe("checklist.not_applicable_refused");

    [Fact]
    public void Pre_op_items_can_be_reset_before_sign_in()
    {
        var booking = TestData.ReadyForSignIn();

        booking.RecordPreOp([new ChecklistInput("npo", ChecklistItemState.Pending, null)], TestData.Staff()).IsSuccess.ShouldBeTrue();

        PreOpChecklist.IsComplete(booking.PreOpChecks).ShouldBeFalse();
        booking.PreOpChecks.Single(e => e.Key == "npo").RecordedAt.ShouldBeNull();
    }
}
