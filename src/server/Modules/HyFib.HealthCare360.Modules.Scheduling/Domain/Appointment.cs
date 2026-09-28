using System.Linq.Expressions;

namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

public enum AppointmentStatus
{
    Booked = 0,

    /// <summary>Confirmed by phone or by online prepayment.</summary>
    Confirmed,

    /// <summary>Arrived; a visit (and queue token) exists.</summary>
    CheckedIn,

    InConsultation,
    Completed,
    Cancelled,
    NoShow,

    /// <summary>Moved to another slot; the replacement appointment carries <see cref="Appointment.RescheduledFromId"/>.</summary>
    Rescheduled,
}

/// <summary>Where the booking came from (PMS-02, OA-*); drives reports and reminder wording.</summary>
public enum AppointmentChannel
{
    WalkIn = 0,
    Phone,
    Online,
    Portal,
    Kiosk,
    WhatsApp,
}

public enum AppointmentVisitType
{
    New = 0,
    FollowUp,
}

public enum AppointmentPaymentStatus
{
    Unpaid = 0,
    PaidOnline,
    PayAtHospital,
}

/// <summary>Coded cancellation reasons (reported in MIS); free text adds detail.</summary>
public enum CancellationReason
{
    PatientRequest = 0,
    DoctorUnavailable,
    HospitalRequest,
    Duplicate,
    PaymentNotReceived,
    Other,
}

public enum CancellationSource
{
    Staff = 0,
    Patient,
    System,
}

public enum AppointmentReminder
{
    DayBefore = 0,
    TwoHoursBefore,
}

/// <summary>Everything <see cref="Appointment.Book"/> needs, already checked against the slot engine.</summary>
public sealed record AppointmentBooking(
    Guid FacilityId,
    Guid PatientId,
    Guid PractitionerId,
    Guid DepartmentId,
    Guid? ScheduleId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int SeatNumber,
    AppointmentChannel Channel,
    AppointmentVisitType VisitType,
    bool IsTelehealth,
    string? Room,
    decimal FeeAmount,
    string? Reason,
    string? Notes,
    Guid? BookedByUserId);

/// <summary>
/// A reserved consultation slot with a practitioner (PMS-02, OA-01..05, TEL-01). State changes happen only through
/// the methods below so that every channel (front desk, phone, portal, waitlist, reminders) follows the same rules.
/// Rescheduling never edits the time of a booking: the old row becomes <see cref="AppointmentStatus.Rescheduled"/>
/// and a new row points back to it, so the history of changes is kept. <see cref="SeatNumber"/> (1..capacity) backs
/// a partial unique index that makes over-booking impossible even if two desks book the same slot at once.
/// </summary>
public sealed class Appointment : AuditableEntity, IFacilityScoped
{
    /// <summary>Appointments that still hold their seat in the slot (EF-translatable).</summary>
    public static Expression<Func<Appointment, bool>> HoldsSeatExpression { get; } =
        a => a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.Rescheduled;

    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid PractitionerId { get; set; }
    public Guid DepartmentId { get; set; }

    /// <summary>The recurring session the slot came from (null for extra sessions).</summary>
    public Guid? ScheduleId { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public int SeatNumber { get; set; } = 1;
    public AppointmentStatus Status { get; set; }
    public AppointmentChannel Channel { get; set; }
    public AppointmentVisitType VisitType { get; set; }
    public bool IsTelehealth { get; set; }
    public string? Room { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset BookedAt { get; set; }
    public Guid? BookedByUserId { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>Consultation fee quoted at booking (INR); the bill is raised by Billing.</summary>
    public decimal FeeAmount { get; set; }

    public AppointmentPaymentStatus PaymentStatus { get; set; }
    public string? PaymentProvider { get; set; }
    public string? GatewayOrderId { get; set; }
    public string? GatewayPaymentId { get; set; }
    public decimal? AmountPaid { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    public Guid? VisitId { get; set; }
    public DateTimeOffset? CheckedInAt { get; set; }
    public DateTimeOffset? ConsultationStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? NoShowAt { get; set; }

    public CancellationReason? CancelReasonCode { get; set; }
    public string? CancelReason { get; set; }
    public CancellationSource? CancelledBy { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? RescheduledFromId { get; set; }
    public Guid? RescheduledToId { get; set; }

    /// <summary>Set when the booking fulfilled a waitlist request.</summary>
    public Guid? WaitlistEntryId { get; set; }

    /// <summary>When the 24-hour reminder was sent (or deliberately skipped); never sent twice.</summary>
    public DateTimeOffset? Reminder24hSentAt { get; set; }

    /// <summary>When the 2-hour reminder was sent (or deliberately skipped); never sent twice.</summary>
    public DateTimeOffset? Reminder2hSentAt { get; set; }

    /// <summary>Still holds its seat (everything except cancelled and rescheduled).</summary>
    public bool HoldsSeat => Status is not (AppointmentStatus.Cancelled or AppointmentStatus.Rescheduled);

    /// <summary>Booked or confirmed: the patient has not arrived yet, so it may still be changed.</summary>
    public bool IsOpen => Status is AppointmentStatus.Booked or AppointmentStatus.Confirmed;

    /// <summary>Creates the booking and notifies other modules (VirtualCare creates the video session for telehealth).</summary>
    public static Appointment Book(AppointmentBooking booking, string number, DateTimeOffset now)
    {
        var appointment = new Appointment
        {
            FacilityId = booking.FacilityId,
            Number = number,
            PatientId = booking.PatientId,
            PractitionerId = booking.PractitionerId,
            DepartmentId = booking.DepartmentId,
            ScheduleId = booking.ScheduleId,
            StartsAt = booking.StartsAt,
            EndsAt = booking.EndsAt,
            SeatNumber = booking.SeatNumber,
            Status = AppointmentStatus.Booked,
            Channel = booking.Channel,
            VisitType = booking.VisitType,
            IsTelehealth = booking.IsTelehealth,
            Room = booking.Room,
            FeeAmount = booking.FeeAmount,
            Reason = booking.Reason,
            Notes = booking.Notes,
            BookedAt = now,
            BookedByUserId = booking.BookedByUserId,
            PaymentStatus = AppointmentPaymentStatus.Unpaid,
        };
        appointment.Raise(new AppointmentBooked(
            appointment.Id, appointment.PatientId, appointment.PractitionerId, appointment.StartsAt, appointment.Channel.ToString(), appointment.IsTelehealth));
        return appointment;
    }

    public Result Confirm(DateTimeOffset now)
    {
        if (Status == AppointmentStatus.Confirmed)
        {
            return Result.Success();
        }

        if (Status != AppointmentStatus.Booked)
        {
            return NotOpen("confirmed");
        }

        Status = AppointmentStatus.Confirmed;
        ConfirmedAt = now;
        return Result.Success();
    }

    /// <summary>Front-desk check-in is allowed on the day of the appointment only (early or late arrival is fine).</summary>
    public Result EnsureCanCheckIn(DateOnly today, HospitalCalendar calendar)
    {
        if (Status is AppointmentStatus.CheckedIn or AppointmentStatus.InConsultation)
        {
            return Error.Conflict("appointment.already_checked_in", "This appointment is already checked in.", new { visitId = VisitId });
        }

        if (!IsOpen)
        {
            return NotOpen("checked in");
        }

        return calendar.DateOf(StartsAt) == today
            ? Result.Success()
            : Error.Rule("appointment.not_today", "Only today's appointments can be checked in. Reschedule it to today first.");
    }

    /// <summary>Links the visit created at check-in (PMS-03). Repeating it for the same visit is harmless.</summary>
    public Result CheckIn(Guid visitId, DateTimeOffset now)
    {
        if (VisitId == visitId && Status is AppointmentStatus.CheckedIn or AppointmentStatus.InConsultation or AppointmentStatus.Completed)
        {
            return Result.Success();
        }

        if (!IsOpen)
        {
            return NotOpen("checked in");
        }

        Status = AppointmentStatus.CheckedIn;
        VisitId = visitId;
        CheckedInAt = now;
        return Result.Success();
    }

    /// <summary>Reverts a check-in whose visit was cancelled at the desk (for example checked in by mistake).</summary>
    public Result UndoCheckIn(Guid visitId)
    {
        if (VisitId != visitId || Status != AppointmentStatus.CheckedIn)
        {
            return Error.Rule("appointment.not_checked_in", "This appointment is not checked in with that visit.");
        }

        Status = ConfirmedAt is null ? AppointmentStatus.Booked : AppointmentStatus.Confirmed;
        VisitId = null;
        CheckedInAt = null;
        return Result.Success();
    }

    public Result StartConsultation(DateTimeOffset now)
    {
        if (Status == AppointmentStatus.InConsultation)
        {
            return Result.Success();
        }

        if (Status != AppointmentStatus.CheckedIn)
        {
            return Error.Rule("appointment.not_checked_in", "Check the patient in before starting the consultation.");
        }

        Status = AppointmentStatus.InConsultation;
        ConsultationStartedAt = now;
        return Result.Success();
    }

    public Result Complete(DateTimeOffset now)
    {
        if (Status == AppointmentStatus.Completed)
        {
            return Result.Success();
        }

        if (Status is not (AppointmentStatus.CheckedIn or AppointmentStatus.InConsultation))
        {
            return Error.Rule("appointment.not_checked_in", "Only checked-in appointments can be completed.");
        }

        Status = AppointmentStatus.Completed;
        CompletedAt = now;
        return Result.Success();
    }

    public Result Cancel(CancellationReason code, string? reason, CancellationSource by, Guid? userId, DateTimeOffset now)
    {
        if (Status == AppointmentStatus.Cancelled)
        {
            return Error.Rule("appointment.already_cancelled", "This appointment is already cancelled.");
        }

        if (!IsOpen)
        {
            return Status is AppointmentStatus.CheckedIn or AppointmentStatus.InConsultation
                ? Error.Rule("appointment.checked_in", "The patient has already checked in. Cancel the visit instead.")
                : NotOpen("cancelled");
        }

        if (code == CancellationReason.Other && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Describe why the appointment is cancelled.");
        }

        Status = AppointmentStatus.Cancelled;
        CancelReasonCode = code;
        CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        CancelledBy = by;
        CancelledByUserId = userId;
        CancelledAt = now;
        Raise(new AppointmentCancelled(Id, PatientId, CancelReason ?? Describe(code)));
        return Result.Success();
    }

    /// <summary>
    /// Moves this booking to <paramref name="replacement"/>: this row keeps its original time with status
    /// Rescheduled; online prepayment moves to the new booking. Other modules see the old booking as cancelled.
    /// </summary>
    public Result MarkRescheduled(Appointment replacement, string? reason, Guid? userId, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return NotOpen("rescheduled");
        }

        Status = AppointmentStatus.Rescheduled;
        RescheduledToId = replacement.Id;
        CancelReason = string.IsNullOrWhiteSpace(reason) ? $"Rescheduled to {replacement.Number}" : reason.Trim();
        CancelledByUserId = userId;
        CancelledAt = now;

        replacement.RescheduledFromId = Id;
        if (PaymentStatus == AppointmentPaymentStatus.PaidOnline)
        {
            replacement.PaymentStatus = AppointmentPaymentStatus.PaidOnline;
            replacement.PaymentProvider = PaymentProvider;
            replacement.GatewayOrderId = GatewayOrderId;
            replacement.GatewayPaymentId = GatewayPaymentId;
            replacement.AmountPaid = AmountPaid;
            replacement.PaidAt = PaidAt;
            replacement.Status = AppointmentStatus.Confirmed;
            replacement.ConfirmedAt = now;
        }
        else if (PaymentStatus == AppointmentPaymentStatus.PayAtHospital)
        {
            replacement.PaymentStatus = AppointmentPaymentStatus.PayAtHospital;
        }

        Raise(new AppointmentCancelled(Id, PatientId, $"Rescheduled to {replacement.Number}"));
        return Result.Success();
    }

    public Result MarkNoShow(DateTimeOffset now)
    {
        if (Status == AppointmentStatus.NoShow)
        {
            return Result.Success();
        }

        if (!IsOpen)
        {
            return NotOpen("marked as a no-show");
        }

        if (now < StartsAt)
        {
            return Error.Rule("appointment.not_started", "A no-show can be recorded only after the appointment time.");
        }

        Status = AppointmentStatus.NoShow;
        NoShowAt = now;
        return Result.Success();
    }

    public Result AttachPaymentOrder(string provider, string orderId)
    {
        if (PaymentStatus == AppointmentPaymentStatus.PaidOnline)
        {
            return Error.Conflict("appointment.already_paid", "This appointment is already paid.");
        }

        if (!IsOpen)
        {
            return NotOpen("paid for");
        }

        PaymentProvider = provider;
        GatewayOrderId = orderId;
        PaymentStatus = AppointmentPaymentStatus.Unpaid;
        return Result.Success();
    }

    /// <summary>
    /// Records a verified online payment. A payment that arrives after the booking was cancelled is still recorded
    /// (money was taken) so Billing can refund it; only an open booking becomes Confirmed.
    /// </summary>
    public Result MarkPaidOnline(string orderId, string paymentId, decimal amount, DateTimeOffset now)
    {
        if (PaymentStatus == AppointmentPaymentStatus.PaidOnline)
        {
            return string.Equals(GatewayPaymentId, paymentId, StringComparison.Ordinal)
                ? Result.Success()
                : Error.Conflict("appointment.already_paid", "This appointment is already paid.");
        }

        if (GatewayOrderId is null || !string.Equals(GatewayOrderId, orderId, StringComparison.Ordinal))
        {
            return Error.Rule("payment.order_mismatch", "This payment does not belong to this appointment.");
        }

        PaymentStatus = AppointmentPaymentStatus.PaidOnline;
        GatewayPaymentId = paymentId;
        AmountPaid = Money.Round(amount);
        PaidAt = now;
        if (Status == AppointmentStatus.Booked)
        {
            Status = AppointmentStatus.Confirmed;
            ConfirmedAt = now;
        }

        Raise(new AppointmentPaymentCaptured(Id, Number, PatientId, PractitionerId, FacilityId, AmountPaid.Value, PaymentProvider, orderId, paymentId, now));
        return Result.Success();
    }

    /// <summary>The patient will pay at the counter (chosen, or the payment gateway is unavailable).</summary>
    public void ChoosePayAtHospital()
    {
        if (PaymentStatus != AppointmentPaymentStatus.PaidOnline)
        {
            PaymentStatus = AppointmentPaymentStatus.PayAtHospital;
        }
    }

    public void MarkReminderSent(AppointmentReminder reminder, DateTimeOffset now)
    {
        if (reminder == AppointmentReminder.DayBefore)
        {
            Reminder24hSentAt ??= now;
        }
        else
        {
            Reminder2hSentAt ??= now;
        }
    }

    public static string Describe(CancellationReason code) => code switch
    {
        CancellationReason.PatientRequest => "Cancelled at the patient's request",
        CancellationReason.DoctorUnavailable => "Doctor unavailable",
        CancellationReason.HospitalRequest => "Cancelled by the hospital",
        CancellationReason.Duplicate => "Duplicate booking",
        CancellationReason.PaymentNotReceived => "Payment not received",
        _ => "Cancelled",
    };

    private Error NotOpen(string action) => Error.Rule("appointment.not_open", Status switch
    {
        AppointmentStatus.Cancelled => $"A cancelled appointment cannot be {action}.",
        AppointmentStatus.Rescheduled => $"This appointment was rescheduled; the new appointment must be {action} instead.",
        AppointmentStatus.Completed => $"A completed appointment cannot be {action}.",
        AppointmentStatus.NoShow => $"A no-show appointment cannot be {action}.",
        AppointmentStatus.CheckedIn or AppointmentStatus.InConsultation => $"The patient has already arrived; the appointment cannot be {action}.",
        _ => $"This appointment cannot be {action}.",
    });
}

/// <summary>
/// An online prepayment for an appointment was verified (OA-03). Billing can handle it to issue an advance receipt;
/// Scheduling itself never posts to billing.
/// </summary>
public sealed record AppointmentPaymentCaptured(
    Guid AppointmentId,
    string AppointmentNumber,
    Guid PatientId,
    Guid PractitionerId,
    Guid FacilityId,
    decimal Amount,
    string? Provider,
    string GatewayOrderId,
    string GatewayPaymentId,
    DateTimeOffset PaidAt) : DomainEvent;
