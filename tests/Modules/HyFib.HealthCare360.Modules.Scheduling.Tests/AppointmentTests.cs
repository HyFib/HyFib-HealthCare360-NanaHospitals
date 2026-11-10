using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Scheduling.Tests.TestSchedules;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

public sealed class AppointmentTests
{
    private static readonly DateTimeOffset Start = Ist(Monday, 10, 0);

    [Fact]
    public void Booking_holds_a_seat_and_announces_itself_to_other_modules()
    {
        var appointment = Booked(Start, AppointmentChannel.Portal, telehealth: true);

        appointment.Status.ShouldBe(AppointmentStatus.Booked);
        appointment.PaymentStatus.ShouldBe(AppointmentPaymentStatus.Unpaid);
        appointment.HoldsSeat.ShouldBeTrue();
        var booked = appointment.DomainEvents.OfType<AppointmentBooked>().ShouldHaveSingleItem();
        booked.AppointmentId.ShouldBe(appointment.Id);
        booked.Channel.ShouldBe("Portal");
        booked.IsTelehealth.ShouldBeTrue();
        booked.StartsAt.ShouldBe(Start);
    }

    [Fact]
    public void Cancelling_frees_the_seat_and_raises_AppointmentCancelled()
    {
        var appointment = Booked(Start);
        appointment.ClearDomainEvents();

        var result = appointment.Cancel(CancellationReason.DoctorUnavailable, null, CancellationSource.Staff, null, Start.AddHours(-3));

        result.IsSuccess.ShouldBeTrue();
        appointment.Status.ShouldBe(AppointmentStatus.Cancelled);
        appointment.HoldsSeat.ShouldBeFalse();
        appointment.CancelReasonCode.ShouldBe(CancellationReason.DoctorUnavailable);
        appointment.DomainEvents.OfType<AppointmentCancelled>().ShouldHaveSingleItem().Reason.ShouldBe("Doctor unavailable");
    }

    [Fact]
    public void Cancelling_twice_is_rejected()
    {
        var appointment = Booked(Start);
        appointment.Cancel(CancellationReason.PatientRequest, null, CancellationSource.Patient, null, Start.AddHours(-3));

        appointment.Cancel(CancellationReason.PatientRequest, null, CancellationSource.Patient, null, Start.AddHours(-2))
            .Error!.Code.ShouldBe("appointment.already_cancelled");
    }

    [Fact]
    public void Other_as_a_cancellation_reason_needs_an_explanation()
    {
        var appointment = Booked(Start);

        var result = appointment.Cancel(CancellationReason.Other, "  ", CancellationSource.Staff, null, Start.AddHours(-3));

        result.Error!.Type.ShouldBe(ErrorType.Validation);
        appointment.Status.ShouldBe(AppointmentStatus.Booked);
    }

    /// <summary>
    /// Gap #35, decided by the hospital: a booking is never cancelled for non-payment by HC360. "Payment not received" is
    /// a person's decision at the desk, so the patient, and anything that runs on its own with no one signed in, is
    /// refused that reason, and the booking keeps its slot.
    /// </summary>
    [Theory]
    [InlineData(CancellationSource.Patient, true)]
    [InlineData(CancellationSource.System, false)]
    [InlineData(CancellationSource.System, true)]
    [InlineData(CancellationSource.Staff, false)]
    public void Payment_not_received_is_a_reason_only_signed_in_staff_can_give(CancellationSource by, bool signedIn)
    {
        var appointment = Booked(Start, AppointmentChannel.Portal);
        appointment.ClearDomainEvents();

        var result = appointment.Cancel(CancellationReason.PaymentNotReceived, null, by, signedIn ? Guid.CreateVersion7() : null, Start.AddHours(-3));

        result.Error!.Code.ShouldBe("appointment.payment_cancel_staff_only");
        result.Error.Type.ShouldBe(ErrorType.BusinessRule);
        appointment.Status.ShouldBe(AppointmentStatus.Booked);
        appointment.HoldsSeat.ShouldBeTrue();
        appointment.CancelReasonCode.ShouldBeNull();
        appointment.DomainEvents.OfType<AppointmentCancelled>().ShouldBeEmpty();
    }

    [Fact]
    public void Staff_at_the_desk_may_cancel_a_booking_because_the_payment_was_not_received()
    {
        var appointment = Booked(Start, AppointmentChannel.Portal);
        appointment.ClearDomainEvents();
        var clerk = Guid.CreateVersion7();

        appointment.Cancel(CancellationReason.PaymentNotReceived, null, CancellationSource.Staff, clerk, Start.AddHours(-3)).IsSuccess.ShouldBeTrue();

        appointment.Status.ShouldBe(AppointmentStatus.Cancelled);
        appointment.CancelledBy.ShouldBe(CancellationSource.Staff);
        appointment.CancelledByUserId.ShouldBe(clerk);
        appointment.DomainEvents.OfType<AppointmentCancelled>().ShouldHaveSingleItem().Reason.ShouldBe("Payment not received");
    }

    [Fact]
    public void A_checked_in_appointment_cannot_be_cancelled()
    {
        var appointment = Booked(Start);
        appointment.CheckIn(Guid.CreateVersion7(), Start.AddMinutes(-20));

        appointment.Cancel(CancellationReason.PatientRequest, null, CancellationSource.Staff, null, Start)
            .Error!.Code.ShouldBe("appointment.checked_in");
    }

    [Fact]
    public void Check_in_links_the_visit_and_is_idempotent_for_the_same_visit()
    {
        var appointment = Booked(Start);
        var visitId = Guid.CreateVersion7();

        appointment.CheckIn(visitId, Start.AddMinutes(-10)).IsSuccess.ShouldBeTrue();
        appointment.CheckIn(visitId, Start.AddMinutes(-5)).IsSuccess.ShouldBeTrue();

        appointment.Status.ShouldBe(AppointmentStatus.CheckedIn);
        appointment.VisitId.ShouldBe(visitId);
        appointment.CheckedInAt.ShouldBe(Start.AddMinutes(-10));
        appointment.CheckIn(Guid.CreateVersion7(), Start).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Front_desk_check_in_is_allowed_only_on_the_appointment_day()
    {
        var appointment = Booked(Start);

        appointment.EnsureCanCheckIn(Monday, Calendar).IsSuccess.ShouldBeTrue();
        appointment.EnsureCanCheckIn(Monday.AddDays(-1), Calendar).Error!.Code.ShouldBe("appointment.not_today");
    }

    [Fact]
    public void Undoing_a_check_in_restores_the_previous_status()
    {
        var appointment = Booked(Start);
        appointment.Confirm(Start.AddDays(-1));
        var visitId = Guid.CreateVersion7();
        appointment.CheckIn(visitId, Start);

        appointment.UndoCheckIn(visitId).IsSuccess.ShouldBeTrue();

        appointment.Status.ShouldBe(AppointmentStatus.Confirmed);
        appointment.VisitId.ShouldBeNull();
    }

    [Fact]
    public void Consultation_follows_check_in_and_completes()
    {
        var appointment = Booked(Start);

        appointment.StartConsultation(Start).Error!.Code.ShouldBe("appointment.not_checked_in");
        appointment.Complete(Start).Error!.Code.ShouldBe("appointment.not_checked_in");

        appointment.CheckIn(Guid.CreateVersion7(), Start);
        appointment.StartConsultation(Start.AddMinutes(5)).IsSuccess.ShouldBeTrue();
        appointment.Complete(Start.AddMinutes(20)).IsSuccess.ShouldBeTrue();
        appointment.Status.ShouldBe(AppointmentStatus.Completed);
    }

    [Fact]
    public void A_no_show_can_be_recorded_only_after_the_appointment_time()
    {
        var appointment = Booked(Start);

        appointment.MarkNoShow(Start.AddMinutes(-1)).Error!.Code.ShouldBe("appointment.not_started");
        appointment.MarkNoShow(Start.AddMinutes(30)).IsSuccess.ShouldBeTrue();
        appointment.Status.ShouldBe(AppointmentStatus.NoShow);
        appointment.HoldsSeat.ShouldBeTrue();
    }

    [Fact]
    public void Rescheduling_keeps_the_old_booking_as_history()
    {
        var original = Booked(Start, number: "NHAP260000001");
        var replacement = Booked(Start.AddDays(1), number: "NHAP260000002");
        original.ClearDomainEvents();

        var result = original.MarkRescheduled(replacement, null, null, Start.AddDays(-1));

        result.IsSuccess.ShouldBeTrue();
        original.Status.ShouldBe(AppointmentStatus.Rescheduled);
        original.StartsAt.ShouldBe(Start);
        original.RescheduledToId.ShouldBe(replacement.Id);
        replacement.RescheduledFromId.ShouldBe(original.Id);
        original.HoldsSeat.ShouldBeFalse();
        original.DomainEvents.OfType<AppointmentCancelled>().ShouldHaveSingleItem().Reason.ShouldBe("Rescheduled to NHAP260000002");
    }

    [Fact]
    public void Rescheduling_carries_an_online_prepayment_to_the_new_booking()
    {
        var original = Booked(Start, AppointmentChannel.Portal);
        original.AttachPaymentOrder("razorpay", "order_1", Start.AddDays(-3)).IsSuccess.ShouldBeTrue();
        original.MarkPaidOnline("order_1", "pay_1", 600m, Start.AddDays(-2)).IsSuccess.ShouldBeTrue();
        var replacement = Booked(Start.AddDays(1), AppointmentChannel.Portal, number: "NHAP260000002");

        original.MarkRescheduled(replacement, "Travel plans changed", null, Start.AddDays(-1));

        replacement.PaymentStatus.ShouldBe(AppointmentPaymentStatus.PaidOnline);
        replacement.GatewayPaymentId.ShouldBe("pay_1");
        replacement.Status.ShouldBe(AppointmentStatus.Confirmed);
        original.CancelReason.ShouldBe("Travel plans changed");
    }

    [Fact]
    public void Verified_online_payment_confirms_the_booking_and_notifies_billing()
    {
        var appointment = Booked(Start, AppointmentChannel.Portal);
        appointment.AttachPaymentOrder("razorpay", "order_9", Start.AddDays(-3));
        appointment.ClearDomainEvents();

        appointment.MarkPaidOnline("order_9", "pay_9", 600m, Start.AddDays(-1)).IsSuccess.ShouldBeTrue();

        appointment.Status.ShouldBe(AppointmentStatus.Confirmed);
        appointment.PaymentStatus.ShouldBe(AppointmentPaymentStatus.PaidOnline);
        var captured = appointment.DomainEvents.OfType<AppointmentPaymentCaptured>().ShouldHaveSingleItem();
        captured.Amount.ShouldBe(600m);
        captured.GatewayPaymentId.ShouldBe("pay_9");
    }

    [Fact]
    public void Payment_for_another_order_is_rejected_and_repeats_are_harmless()
    {
        var appointment = Booked(Start, AppointmentChannel.Portal);
        appointment.AttachPaymentOrder("razorpay", "order_9", Start.AddDays(-3));

        appointment.MarkPaidOnline("order_X", "pay_9", 600m, Start).Error!.Code.ShouldBe("payment.order_mismatch");
        appointment.MarkPaidOnline("order_9", "pay_9", 600m, Start).IsSuccess.ShouldBeTrue();
        appointment.MarkPaidOnline("order_9", "pay_9", 600m, Start).IsSuccess.ShouldBeTrue();
        appointment.MarkPaidOnline("order_9", "pay_other", 600m, Start).Error!.Code.ShouldBe("appointment.already_paid");
    }

    [Fact]
    public void Choosing_to_pay_at_the_hospital_never_downgrades_a_paid_booking()
    {
        var appointment = Booked(Start, AppointmentChannel.Portal);
        appointment.AttachPaymentOrder("razorpay", "order_9", Start.AddDays(-3));
        appointment.MarkPaidOnline("order_9", "pay_9", 600m, Start);

        appointment.ChoosePayAtHospital();

        appointment.PaymentStatus.ShouldBe(AppointmentPaymentStatus.PaidOnline);
    }
}
