using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Flags;

public sealed record RecordAllergyRequest(string Allergen, AllergyType Type, string? Reaction, AllergySeverity Severity, DateOnly? OnsetDate, string? Notes);

/// <param name="ChangeReason">
/// Why, when the change waits for a second clinician (a lower severity; a status change gives its reason in
/// <paramref name="StatusReason"/>).
/// </param>
public sealed record UpdateAllergyRequest(
    string? Reaction, AllergySeverity Severity, AllergyStatus Status, string? StatusReason, string? Notes, uint Version, string? ChangeReason = null);

public sealed record AllergyDto(
    Guid Id,
    Guid PatientId,
    string Allergen,
    AllergyType Type,
    string? Reaction,
    AllergySeverity Severity,
    AllergyStatus Status,
    string? StatusReason,
    DateOnly? OnsetDate,
    DateTimeOffset RecordedAt,
    Guid? RecordedByUserId,
    string? Notes,
    uint Version,
    PendingFlagChange? PendingChange = null);

/// <summary>
/// What the banner may claim about allergies. <c>Recorded</c>: active allergies exist (time and author of the most
/// recently recorded one). <c>NoKnownAllergies</c>: a clinician recorded "no known allergies" (time and author of that
/// statement). <c>NotRecorded</c>: neither (both null).
/// </summary>
public sealed record AllergyStatusDto(AllergyRecordingStatus Status, DateTimeOffset? RecordedAt, string? RecordedByName);

public sealed record RaiseAlertRequest(PatientAlertType Type, string? Title, string? Detail, AlertSeverity? Severity, DateOnly? ExpiresOn);

public sealed record DeactivateAlertRequest(string Reason);

public sealed record PatientAlertDto(
    Guid Id,
    Guid PatientId,
    PatientAlertType Type,
    string Title,
    string? Detail,
    AlertSeverity Severity,
    DateTimeOffset EffectiveFrom,
    DateOnly? ExpiresOn,
    bool IsActive,
    bool IsCurrent,
    DateTimeOffset? DeactivatedAt,
    string? DeactivationReason,
    PendingFlagChange? PendingChange = null);

public sealed class RecordAllergyRequestValidator : AbstractValidator<RecordAllergyRequest>
{
    public RecordAllergyRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Allergen).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Reaction).SafeText(300);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.OnsetDate).Must(d => d is null || d <= time.Today().AddDays(1))
            .WithMessage("The onset date cannot be in the future.");
    }
}

public sealed class UpdateAllergyRequestValidator : AbstractValidator<UpdateAllergyRequest>
{
    public UpdateAllergyRequestValidator()
    {
        RuleFor(x => x.Reaction).SafeText(300);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.StatusReason).SafeText(300);
        RuleFor(x => x.StatusReason).NotEmpty().When(x => x.Status != AllergyStatus.Active)
            .WithMessage("Give a reason: this stops the allergy from appearing in prescribing checks.");
        RuleFor(x => x.ChangeReason).SafeText(300);
    }
}

public sealed class RaiseAlertRequestValidator : AbstractValidator<RaiseAlertRequest>
{
    public RaiseAlertRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().When(x => x.Type == PatientAlertType.Custom).WithMessage("Give the alert a title.");
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.Detail).SafeText(500);
        RuleFor(x => x.Detail).NotEmpty().When(x => x.Type == PatientAlertType.InfectionControl)
            .WithMessage("Name the organism or infection (for example MRSA, open TB).");

        // An end date would take DNR off on its own, without the second doctor that taking it off needs (slice 7).
        RuleFor(x => x.ExpiresOn).Null().When(x => x.Type == PatientAlertType.Dnr)
            .WithMessage("A DNR order has no end date: it stays until a doctor takes it off with a second doctor's agreement.");
    }
}

public sealed class DeactivateAlertRequestValidator : AbstractValidator<DeactivateAlertRequest>
{
    public DeactivateAlertRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}
