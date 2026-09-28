namespace HyFib.HealthCare360.Modules.Clinical.Features.Immunizations;

public sealed record RecordImmunizationRequest(
    Guid? EncounterId,
    string VaccineName,
    string? VaccineCode,
    int? DoseNumber,
    DateOnly AdministeredOn,
    string? Route,
    string? Site,
    string? BatchNumber,
    string? Manufacturer,
    DateOnly? ExpiryDate,
    string? AdministeredBy,
    bool IsExternal,
    DateOnly? NextDueOn,
    string? Notes);

public sealed record ImmunizationDto(
    Guid Id,
    Guid PatientId,
    Guid? EncounterId,
    string VaccineName,
    string? VaccineCode,
    int? DoseNumber,
    DateOnly AdministeredOn,
    string? Route,
    string? Site,
    string? BatchNumber,
    string? Manufacturer,
    DateOnly? ExpiryDate,
    string? AdministeredBy,
    bool IsExternal,
    DateOnly? NextDueOn,
    string? Notes,
    bool IsEnteredInError,
    string? EnteredInErrorReason);

/// <summary>
/// Doses given here must carry batch and expiry (traceability for adverse events following immunisation, AEFI), and a
/// dose recorded after the batch expired is rejected. Transcribed doses from a card only need the vaccine and date.
/// </summary>
public sealed class RecordImmunizationRequestValidator : AbstractValidator<RecordImmunizationRequest>
{
    public RecordImmunizationRequestValidator(TimeProvider time)
    {
        var tomorrow = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(1);
        RuleFor(x => x.VaccineName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.VaccineCode).MaximumLength(32);
        RuleFor(x => x.DoseNumber).InclusiveBetween(1, 10);
        RuleFor(x => x.AdministeredOn).LessThanOrEqualTo(tomorrow).WithMessage("The date cannot be in the future.");
        RuleFor(x => x.Route).MaximumLength(32);
        RuleFor(x => x.Site).MaximumLength(64);
        RuleFor(x => x.BatchNumber).MaximumLength(64);
        RuleFor(x => x.Manufacturer).MaximumLength(128);
        RuleFor(x => x.AdministeredBy).MaximumLength(200);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.BatchNumber).NotEmpty().When(x => !x.IsExternal).WithMessage("Record the batch number of the vial used.");
        RuleFor(x => x.ExpiryDate).NotNull().When(x => !x.IsExternal).WithMessage("Record the expiry date of the batch.");
        RuleFor(x => x.ExpiryDate).GreaterThanOrEqualTo(x => x.AdministeredOn).When(x => !x.IsExternal && x.ExpiryDate is not null)
            .WithMessage("This batch had expired on the day it was given. Do not record expired vaccine as a valid dose; report it as an incident.");
        RuleFor(x => x.NextDueOn).GreaterThan(x => x.AdministeredOn).When(x => x.NextDueOn is not null)
            .WithMessage("The next dose must be due after this dose.");
    }
}
