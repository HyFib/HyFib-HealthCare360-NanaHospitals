using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Flags;

public sealed record RecordAllergyRequest(string Allergen, AllergyType Type, string? Reaction, AllergySeverity Severity, DateOnly? OnsetDate, string? Notes);

public sealed record UpdateAllergyRequest(string? Reaction, AllergySeverity Severity, AllergyStatus Status, string? StatusReason, string? Notes, uint Version);

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
    uint Version);

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
    string? DeactivationReason);

public sealed class RecordAllergyRequestValidator : AbstractValidator<RecordAllergyRequest>
{
    public RecordAllergyRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Allergen).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Reaction).SafeText(300);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.OnsetDate).Must(d => d is null || d <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1))
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
    }
}

public sealed class DeactivateAlertRequestValidator : AbstractValidator<DeactivateAlertRequest>
{
    public DeactivateAlertRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}
