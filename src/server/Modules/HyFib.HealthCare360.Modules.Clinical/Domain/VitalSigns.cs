namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum TemperatureUnit
{
    Celsius = 0,
    Fahrenheit,
}

/// <summary>When a capillary/plasma glucose was taken — the normal range depends on it.</summary>
public enum GlucoseTiming
{
    Random = 0,
    Fasting,
    PostPrandial,
}

/// <summary>
/// One set of vital signs. Recorded against the patient (not only an encounter) because nurses take vitals at triage,
/// in wards and at health-checkup stations before any doctor opens an encounter. Rows are never deleted; a wrong
/// entry is marked "entered in error" so the chart shows what was seen at the time.
/// </summary>
public sealed class VitalSigns : AuditableEntity
{
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? FacilityId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }

    /// <summary>Temperature as entered, in <see cref="TemperatureUnit"/>.</summary>
    public decimal? Temperature { get; set; }

    public TemperatureUnit TemperatureUnit { get; set; }
    public int? Pulse { get; set; }
    public int? RespiratoryRate { get; set; }
    public int? SystolicBp { get; set; }
    public int? DiastolicBp { get; set; }
    public int? SpO2 { get; set; }

    /// <summary>SpO₂ taken on supplemental oxygen (a "normal" saturation on oxygen is still clinically significant).</summary>
    public bool OnSupplementalOxygen { get; set; }

    public decimal? HeightCm { get; set; }
    public decimal? WeightKg { get; set; }

    /// <summary>Computed from height and weight on save (kg/m², one decimal).</summary>
    public decimal? Bmi { get; private set; }

    /// <summary>Numeric pain rating 0–10.</summary>
    public int? PainScore { get; set; }

    /// <summary>Blood glucose in mg/dL.</summary>
    public decimal? BloodGlucose { get; set; }

    public GlucoseTiming GlucoseTiming { get; set; }

    /// <summary>Glasgow Coma Scale total (3–15).</summary>
    public int? Gcs { get; set; }

    /// <summary>Level of consciousness on the ACVPU scale, for NEWS2.</summary>
    public Consciousness? Consciousness { get; set; }

    /// <summary>The NEWS2 oxygen-saturation scale: 2 only when a doctor has prescribed a target of 88–92 %.</summary>
    public SpO2Scale SpO2Scale { get; set; } = SpO2Scale.Scale1;

    public string? Notes { get; set; }
    public bool IsEnteredInError { get; private set; }
    public string? EnteredInErrorReason { get; private set; }

    public decimal? TemperatureCelsius => Temperature is { } t
        ? TemperatureUnit == TemperatureUnit.Fahrenheit ? Math.Round((t - 32m) * 5m / 9m, 1) : t
        : null;

    public void ComputeBmi() => Bmi = BodyMassIndex.Compute(HeightCm, WeightKg);

    public Result MarkEnteredInError(string reason)
    {
        if (IsEnteredInError)
        {
            return Error.Rule("vitals.already_voided", "These vitals are already marked as entered in error.");
        }

        IsEnteredInError = true;
        EnteredInErrorReason = reason.Trim();
        return Result.Success();
    }

    public News2Reading ToNews2Reading() =>
        new(RespiratoryRate, SpO2, SpO2Scale, OnSupplementalOxygen, SystolicBp, Pulse, Consciousness, TemperatureCelsius);

    /// <summary>
    /// Tells the other modules a set of vital signs was taken, with its NEWS2 (null for a child or when nothing NEWS2 reads
    /// was measured): the ward escalates an early-warning score that calls for a doctor. Call once, before saving.
    /// </summary>
    public void MarkRecorded(News2Score? news2) =>
        Raise(new VitalSignsRecorded(Id, PatientId, FacilityId, RecordedAt, RecordedByUserId, news2?.Total, news2?.AnySingleParameterThree ?? false,
            news2?.IsComplete ?? false, news2?.Summary));

    public VitalReading ToReading() => new(
        TemperatureCelsius, Pulse, RespiratoryRate, SystolicBp, DiastolicBp, SpO2, OnSupplementalOxygen, Bmi, PainScore,
        BloodGlucose, GlucoseTiming, Gcs);
}

public static class BodyMassIndex
{
    /// <summary>BMI = kg / m², rounded to one decimal; null unless both values are plausible.</summary>
    public static decimal? Compute(decimal? heightCm, decimal? weightKg)
    {
        if (heightCm is not > 30m || weightKg is not > 0m)
        {
            return null;
        }

        var metres = heightCm.Value / 100m;
        return Math.Round(weightKg.Value / (metres * metres), 1, MidpointRounding.AwayFromZero);
    }
}
