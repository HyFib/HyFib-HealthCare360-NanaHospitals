using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Waitlist;

public sealed record WaitlistEntryDto(
    Guid Id,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string? PhoneMasked,
    Guid PractitionerId,
    string PractitionerName,
    Guid? DepartmentId,
    DateOnly PreferredFrom,
    DateOnly PreferredTo,
    PreferredSession PreferredSession,
    bool IsTelehealth,
    AppointmentChannel Channel,
    string? Notes,
    WaitlistStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? OfferedAt,
    DateTimeOffset? OfferedSlotStartsAt,
    int OfferCount,
    Guid? AppointmentId);

public sealed record AddToWaitlistRequest(
    Guid PatientId,
    Guid PractitionerId,
    DateOnly PreferredFrom,
    DateOnly PreferredTo,
    PreferredSession PreferredSession,
    bool IsTelehealth,
    AppointmentChannel Channel,
    string? Notes);

/// <summary>Books a slot for a waitlisted patient (typically the slot offered after a cancellation).</summary>
public sealed record BookFromWaitlistRequest(DateTimeOffset StartsAt, AppointmentVisitType VisitType, string? Reason, bool Overbook = false);

public sealed class AddToWaitlistRequestValidator : AbstractValidator<AddToWaitlistRequest>
{
    public AddToWaitlistRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty().WithMessage("Choose a patient.");
        RuleFor(x => x.PractitionerId).NotEmpty().WithMessage("Choose a doctor.");
        RuleFor(x => x.PreferredSession).IsInEnum();
        RuleFor(x => x.Channel).IsInEnum();
        RuleFor(x => x.PreferredTo).GreaterThanOrEqualTo(x => x.PreferredFrom).WithMessage("The last date must be on or after the first date.");
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class BookFromWaitlistRequestValidator : AbstractValidator<BookFromWaitlistRequest>
{
    public BookFromWaitlistRequestValidator()
    {
        RuleFor(x => x.StartsAt).NotEqual(default(DateTimeOffset)).WithMessage("Choose a slot.");
        RuleFor(x => x.VisitType).IsInEnum();
        RuleFor(x => x.Reason).SafeText(300);
    }
}
