using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Vitals;

public sealed record RecordVitalsRequest(
    Guid? EncounterId,
    DateTimeOffset? RecordedAt,
    decimal? Temperature,
    TemperatureUnit TemperatureUnit,
    int? Pulse,
    int? RespiratoryRate,
    int? SystolicBp,
    int? DiastolicBp,
    int? SpO2,
    bool OnSupplementalOxygen,
    decimal? HeightCm,
    decimal? WeightKg,
    int? PainScore,
    decimal? BloodGlucose,
    GlucoseTiming GlucoseTiming,
    int? Gcs,
    string? Notes);

/// <summary>Marks a clinical row as recorded by mistake (kept for the audit trail, hidden from summaries).</summary>
public sealed record EnteredInErrorRequest(string Reason);

public sealed record VitalsDto(
    Guid Id,
    Guid PatientId,
    Guid? EncounterId,
    DateTimeOffset RecordedAt,
    Guid? RecordedByUserId,
    decimal? Temperature,
    TemperatureUnit TemperatureUnit,
    decimal? TemperatureCelsius,
    int? Pulse,
    int? RespiratoryRate,
    int? SystolicBp,
    int? DiastolicBp,
    int? SpO2,
    bool OnSupplementalOxygen,
    decimal? HeightCm,
    decimal? WeightKg,
    decimal? Bmi,
    int? PainScore,
    decimal? BloodGlucose,
    GlucoseTiming GlucoseTiming,
    int? Gcs,
    string? Notes,
    bool IsEnteredInError,
    string? EnteredInErrorReason,
    IReadOnlyList<VitalFlag> Flags);

/// <summary>
/// Physiologically plausible bounds: they reject typing errors (a pulse of 720) without judging clinical values,
/// which the assessor flags instead.
/// </summary>
public sealed class RecordVitalsRequestValidator : AbstractValidator<RecordVitalsRequest>
{
    public RecordVitalsRequestValidator(TimeProvider time)
    {
        RuleFor(x => x).Must(x => x.Temperature is not null || x.Pulse is not null || x.RespiratoryRate is not null || x.SystolicBp is not null
                                  || x.DiastolicBp is not null || x.SpO2 is not null || x.HeightCm is not null || x.WeightKg is not null
                                  || x.PainScore is not null || x.BloodGlucose is not null || x.Gcs is not null)
            .WithName("temperature").WithMessage("Enter at least one measurement.");
        RuleFor(x => x.Temperature).InclusiveBetween(25m, 45m).When(x => x.Temperature is not null && x.TemperatureUnit == TemperatureUnit.Celsius)
            .WithMessage("Temperature must be between 25 and 45 °C.");
        RuleFor(x => x.Temperature).InclusiveBetween(77m, 113m).When(x => x.Temperature is not null && x.TemperatureUnit == TemperatureUnit.Fahrenheit)
            .WithMessage("Temperature must be between 77 and 113 °F.");
        RuleFor(x => x.Pulse).InclusiveBetween(20, 300);
        RuleFor(x => x.RespiratoryRate).InclusiveBetween(4, 80);
        RuleFor(x => x.SystolicBp).InclusiveBetween(40, 300);
        RuleFor(x => x.DiastolicBp).InclusiveBetween(20, 200);
        RuleFor(x => x.DiastolicBp).LessThan(x => x.SystolicBp).When(x => x.SystolicBp is not null && x.DiastolicBp is not null)
            .WithMessage("Diastolic pressure must be lower than systolic.");
        RuleFor(x => x.SpO2).InclusiveBetween(30, 100);
        RuleFor(x => x.HeightCm).InclusiveBetween(30m, 250m);
        RuleFor(x => x.WeightKg).InclusiveBetween(0.3m, 400m);
        RuleFor(x => x.PainScore).InclusiveBetween(0, 10);
        RuleFor(x => x.BloodGlucose).InclusiveBetween(10m, 1500m);
        RuleFor(x => x.Gcs).InclusiveBetween(3, 15);
        RuleFor(x => x.RecordedAt)
            .Must(at => at is null || (at <= time.GetUtcNow().AddMinutes(5) && at >= time.GetUtcNow().AddDays(-30)))
            .WithMessage("The time must not be in the future or more than 30 days ago.");
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class EnteredInErrorRequestValidator : AbstractValidator<EnteredInErrorRequest>
{
    public EnteredInErrorRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}
