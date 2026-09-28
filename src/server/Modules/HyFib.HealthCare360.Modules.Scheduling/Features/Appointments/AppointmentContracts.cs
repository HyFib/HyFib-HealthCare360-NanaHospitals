using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;

public sealed record AppointmentListItem(
    Guid Id,
    string Number,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string? PhoneMasked,
    Guid PractitionerId,
    string PractitionerName,
    Guid DepartmentId,
    string? DepartmentName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int SeatNumber,
    AppointmentStatus Status,
    AppointmentChannel Channel,
    AppointmentVisitType VisitType,
    bool IsTelehealth,
    AppointmentPaymentStatus PaymentStatus,
    decimal FeeAmount,
    string? Reason,
    string? Room,
    Guid? VisitId,
    DateTimeOffset BookedAt);

public sealed record AppointmentHistoryItem(Guid Id, string Number, Guid PractitionerId, DateTimeOffset StartsAt, AppointmentStatus Status, DateTimeOffset BookedAt, string? Note);

public sealed record AppointmentDetail(
    Guid Id,
    string Number,
    Guid FacilityId,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string? Phone,
    Guid PractitionerId,
    string PractitionerName,
    Guid DepartmentId,
    string? DepartmentName,
    Guid? ScheduleId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    int SeatNumber,
    AppointmentStatus Status,
    AppointmentChannel Channel,
    AppointmentVisitType VisitType,
    bool IsTelehealth,
    string? Room,
    string? Reason,
    string? Notes,
    DateTimeOffset BookedAt,
    DateTimeOffset? ConfirmedAt,
    decimal FeeAmount,
    AppointmentPaymentStatus PaymentStatus,
    string? PaymentProvider,
    string? GatewayOrderId,
    string? GatewayPaymentId,
    decimal? AmountPaid,
    DateTimeOffset? PaidAt,
    Guid? VisitId,
    DateTimeOffset? CheckedInAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? NoShowAt,
    CancellationReason? CancelReasonCode,
    string? CancelReason,
    CancellationSource? CancelledBy,
    DateTimeOffset? CancelledAt,
    Guid? RescheduledFromId,
    Guid? RescheduledToId,
    Guid? WaitlistEntryId,
    DateTimeOffset? Reminder24hSentAt,
    DateTimeOffset? Reminder2hSentAt,
    IReadOnlyList<AppointmentHistoryItem> History,
    uint Version);

/// <summary>Compact row for day/week calendar views (no paging; the range is capped).</summary>
public sealed record CalendarAppointment(
    Guid Id,
    string Number,
    Guid PatientId,
    string PatientName,
    string Uhid,
    Guid PractitionerId,
    Guid DepartmentId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    AppointmentStatus Status,
    AppointmentVisitType VisitType,
    AppointmentChannel Channel,
    bool IsTelehealth,
    AppointmentPaymentStatus PaymentStatus);

public sealed record PractitionerDayLoad(
    Guid PractitionerId,
    string PractitionerName,
    Guid? DepartmentId,
    int Capacity,
    int Booked,
    int CheckedIn,
    int Completed,
    int NoShow,
    int Cancelled,
    int FreeSlots);

public sealed record AppointmentDaySummary(
    DateOnly Date,
    int Total,
    int Booked,
    int Confirmed,
    int CheckedIn,
    int InConsultation,
    int Completed,
    int Cancelled,
    int NoShow,
    int Telehealth,
    int Online,
    IReadOnlyList<PractitionerDayLoad> Practitioners);

public sealed record BookAppointmentRequest(
    Guid PatientId,
    Guid PractitionerId,
    DateTimeOffset StartsAt,
    AppointmentChannel Channel,
    AppointmentVisitType VisitType,
    bool IsTelehealth,
    string? Reason,
    string? Notes,
    bool Overbook = false,
    bool NotifyPatient = true,
    Guid? WaitlistEntryId = null);

public sealed record RescheduleAppointmentRequest(
    DateTimeOffset StartsAt,
    Guid? PractitionerId,
    bool? IsTelehealth,
    string? Reason,
    bool Overbook = false,
    bool NotifyPatient = true);

public sealed record CancelAppointmentRequest(CancellationReason ReasonCode, string? Reason, bool NotifyPatient = true);

public sealed record CheckInAppointmentRequest(
    PayerType PayerType = PayerType.Self,
    Guid? PayerId = null,
    TokenPriority Priority = TokenPriority.Normal,
    string? ChiefComplaint = null,
    bool IssueToken = true);

public sealed record CheckInResult(Guid AppointmentId, Guid VisitId, string VisitNumber, string? TokenLabel, bool LinkedExistingVisit);

public sealed class BookAppointmentRequestValidator : AbstractValidator<BookAppointmentRequest>
{
    public BookAppointmentRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty().WithMessage("Choose a patient.");
        RuleFor(x => x.PractitionerId).NotEmpty().WithMessage("Choose a doctor.");
        RuleFor(x => x.StartsAt).NotEqual(default(DateTimeOffset)).WithMessage("Choose a slot.");
        RuleFor(x => x.Channel).IsInEnum()
            .NotEqual(AppointmentChannel.Portal).WithMessage("Portal bookings are made by patients from the patient portal.");
        RuleFor(x => x.VisitType).IsInEnum();
        RuleFor(x => x.Reason).SafeText(300);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class RescheduleAppointmentRequestValidator : AbstractValidator<RescheduleAppointmentRequest>
{
    public RescheduleAppointmentRequestValidator()
    {
        RuleFor(x => x.StartsAt).NotEqual(default(DateTimeOffset)).WithMessage("Choose the new slot.");
        RuleFor(x => x.Reason).SafeText(300);
    }
}

public sealed class CancelAppointmentRequestValidator : AbstractValidator<CancelAppointmentRequest>
{
    public CancelAppointmentRequestValidator()
    {
        RuleFor(x => x.ReasonCode).IsInEnum();
        RuleFor(x => x.Reason).NotEmpty().When(x => x.ReasonCode == CancellationReason.Other).WithMessage("Describe why the appointment is cancelled.");
        RuleFor(x => x.Reason).SafeText(300);
    }
}

public sealed class CheckInAppointmentRequestValidator : AbstractValidator<CheckInAppointmentRequest>
{
    public CheckInAppointmentRequestValidator()
    {
        RuleFor(x => x.PayerType).IsInEnum();
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.ChiefComplaint).SafeText(500);
    }
}
