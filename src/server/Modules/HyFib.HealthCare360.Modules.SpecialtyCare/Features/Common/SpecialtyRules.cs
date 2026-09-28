namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

/// <summary>
/// Physiologically plausible ranges shared by the OT, dialysis and physiotherapy validators. They reject typing
/// errors (a BP of 1200) without second-guessing genuinely abnormal values, which the clinician must be able to record.
/// </summary>
public static class SpecialtyRules
{
    public static IRuleBuilderOptions<T, int?> Systolic<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(40, 300).WithMessage("Enter a systolic pressure between 40 and 300 mmHg.");

    public static IRuleBuilderOptions<T, int?> Diastolic<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(20, 200).WithMessage("Enter a diastolic pressure between 20 and 200 mmHg.");

    public static IRuleBuilderOptions<T, int?> Pulse<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(20, 250).WithMessage("Enter a pulse between 20 and 250 per minute.");

    public static IRuleBuilderOptions<T, int?> Saturation<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(40, 100).WithMessage("Enter an SpO₂ between 40 and 100 %.");

    public static IRuleBuilderOptions<T, decimal?> TemperatureC<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.InclusiveBetween(30m, 45m).WithMessage("Enter a temperature between 30 and 45 °C.");

    public static IRuleBuilderOptions<T, decimal?> WeightKg<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.InclusiveBetween(0.5m, 350m).WithMessage("Enter a weight between 0.5 and 350 kg.");

    public static IRuleBuilderOptions<T, int> Systolic<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(40, 300).WithMessage("Enter a systolic pressure between 40 and 300 mmHg.");

    public static IRuleBuilderOptions<T, int> Diastolic<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(20, 200).WithMessage("Enter a diastolic pressure between 20 and 200 mmHg.");

    public static IRuleBuilderOptions<T, int> Pulse<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(20, 250).WithMessage("Enter a pulse between 20 and 250 per minute.");

    public static IRuleBuilderOptions<T, decimal> TemperatureC<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.InclusiveBetween(30m, 45m).WithMessage("Enter a temperature between 30 and 45 °C.");

    public static IRuleBuilderOptions<T, decimal> WeightKg<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.InclusiveBetween(0.5m, 350m).WithMessage("Enter a weight between 0.5 and 350 kg.");

    /// <summary>0–10 numeric rating scale.</summary>
    public static IRuleBuilderOptions<T, int?> PainScore<T>(this IRuleBuilder<T, int?> rule) =>
        rule.InclusiveBetween(0, 10).WithMessage("Pain score must be between 0 and 10.");

    /// <summary>Clinical timestamps may be charted late but never in the future (allowing 5 minutes of clock drift).</summary>
    public static IRuleBuilderOptions<T, DateTimeOffset> NotInFuture<T>(this IRuleBuilder<T, DateTimeOffset> rule, TimeProvider time) =>
        rule.Must(at => at <= time.GetUtcNow().AddMinutes(5)).WithMessage("The time cannot be in the future.");
}
