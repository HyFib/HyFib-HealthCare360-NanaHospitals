using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>Drug names are generic ingredients ("warfarin", "potassium chloride"); they are stored in canonical form.</summary>
public sealed record SaveDrugInteractionRequest(string DrugA, string DrugB, InteractionSeverity Severity, string Effect, string? Management, bool IsActive, uint? Version);

public sealed record DrugInteractionDto(Guid Id, string DrugA, string DrugB, InteractionSeverity Severity, string Effect, string? Management, bool IsActive, uint Version);

public sealed record SaveDoseLimitRequest(string GenericName, decimal MaxDailyDoseMg, string? Route, string? Notes, bool IsActive, uint? Version);

public sealed record DoseLimitDto(Guid Id, string GenericName, decimal MaxDailyDoseMg, string? Route, string? Notes, bool IsActive, uint Version);

public sealed record ControlledDrugRegisterDto(
    Guid Id,
    Guid PrescriptionId,
    string PrescriptionNumber,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid PractitionerId,
    string? PractitionerName,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? Schedule,
    decimal Quantity,
    DateTimeOffset PrescribedAt,
    Guid? ConfirmedByUserId);

public sealed class SaveDrugInteractionRequestValidator : AbstractValidator<SaveDrugInteractionRequest>
{
    public SaveDrugInteractionRequestValidator()
    {
        RuleFor(x => x.DrugA).NotEmpty().MaximumLength(100);
        RuleFor(x => x.DrugB).NotEmpty().MaximumLength(100);
        RuleFor(x => x.DrugB).Must((r, b) => !string.Equals(r.DrugA?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase))
            .WithMessage("Choose two different drugs.");
        RuleFor(x => x.Effect).NotEmpty().SafeText(500);
        RuleFor(x => x.Management).SafeText(500);
    }
}

public sealed class SaveDoseLimitRequestValidator : AbstractValidator<SaveDoseLimitRequest>
{
    public SaveDoseLimitRequestValidator()
    {
        RuleFor(x => x.GenericName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MaxDailyDoseMg).GreaterThan(0).LessThan(1_000_000);
        RuleFor(x => x.Route).MaximumLength(32);
        RuleFor(x => x.Notes).SafeText(300);
    }
}
