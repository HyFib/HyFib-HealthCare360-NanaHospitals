using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Portal;

/// <summary>An appointment as the patient sees it, with the actions the portal may offer right now.</summary>
public sealed record PortalAppointment(
    Guid Id,
    string Number,
    Guid PatientId,
    string PatientName,
    Guid PractitionerId,
    string PractitionerName,
    string? SpecialtyName,
    string? DepartmentName,
    Guid FacilityId,
    string? FacilityName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    AppointmentStatus Status,
    AppointmentVisitType VisitType,
    bool IsTelehealth,
    AppointmentPaymentStatus PaymentStatus,
    decimal FeeAmount,
    string Currency,
    string? Reason,
    bool CanCancel,
    bool CanReschedule,
    bool CanPay);

public sealed record PortalBookAppointmentRequest(
    Guid PatientId,
    Guid PractitionerId,
    DateTimeOffset StartsAt,
    AppointmentVisitType VisitType,
    bool IsTelehealth,
    string? Reason,
    bool PayOnline);

/// <param name="Payment">Gateway checkout details when the patient chose to pay online and the gateway is available.</param>
/// <param name="Message">Shown to the patient, for example when online payment is unavailable and they will pay at the hospital.</param>
public sealed record PortalBookingResult(PortalAppointment Appointment, PaymentCheckout? Payment, string? Message);

public sealed record PortalCancelAppointmentRequest(string? Reason);

public sealed record PortalRescheduleAppointmentRequest(DateTimeOffset StartsAt, string? Reason);

/// <summary>Values returned by the gateway's checkout (Razorpay-style: order id, payment id and signature).</summary>
public sealed record ConfirmPaymentRequest(string OrderId, string PaymentId, string Signature);

public sealed record PortalWaitlistEntry(
    Guid Id,
    Guid PatientId,
    string PatientName,
    Guid PractitionerId,
    string PractitionerName,
    DateOnly PreferredFrom,
    DateOnly PreferredTo,
    PreferredSession PreferredSession,
    bool IsTelehealth,
    WaitlistStatus Status,
    DateTimeOffset? OfferedSlotStartsAt,
    DateTimeOffset CreatedAt);

public sealed record PortalJoinWaitlistRequest(
    Guid PatientId,
    Guid PractitionerId,
    DateOnly PreferredFrom,
    DateOnly PreferredTo,
    PreferredSession PreferredSession,
    bool IsTelehealth,
    string? Notes);

public sealed class PortalBookAppointmentRequestValidator : AbstractValidator<PortalBookAppointmentRequest>
{
    public PortalBookAppointmentRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty().WithMessage("Choose who the appointment is for.");
        RuleFor(x => x.PractitionerId).NotEmpty().WithMessage("Choose a doctor.");
        RuleFor(x => x.StartsAt).NotEqual(default(DateTimeOffset)).WithMessage("Choose a time slot.");
        RuleFor(x => x.VisitType).IsInEnum();
        RuleFor(x => x.Reason).SafeText(300);
    }
}

public sealed class PortalCancelAppointmentRequestValidator : AbstractValidator<PortalCancelAppointmentRequest>
{
    public PortalCancelAppointmentRequestValidator() => RuleFor(x => x.Reason).SafeText(300);
}

public sealed class PortalRescheduleAppointmentRequestValidator : AbstractValidator<PortalRescheduleAppointmentRequest>
{
    public PortalRescheduleAppointmentRequestValidator()
    {
        RuleFor(x => x.StartsAt).NotEqual(default(DateTimeOffset)).WithMessage("Choose the new time slot.");
        RuleFor(x => x.Reason).SafeText(300);
    }
}

public sealed class ConfirmPaymentRequestValidator : AbstractValidator<ConfirmPaymentRequest>
{
    public ConfirmPaymentRequestValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.PaymentId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Signature).NotEmpty().MaximumLength(256);
    }
}

public sealed class PortalJoinWaitlistRequestValidator : AbstractValidator<PortalJoinWaitlistRequest>
{
    public PortalJoinWaitlistRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.PractitionerId).NotEmpty().WithMessage("Choose a doctor.");
        RuleFor(x => x.PreferredSession).IsInEnum();
        RuleFor(x => x.PreferredTo).GreaterThanOrEqualTo(x => x.PreferredFrom).WithMessage("The last date must be on or after the first date.");
        RuleFor(x => x.Notes).SafeText(500);
    }
}
