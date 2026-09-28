namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum FlagDirection
{
    Low = 0,
    High,
}

public enum FlagSeverity
{
    /// <summary>Outside the reference range; review.</summary>
    Abnormal = 0,

    /// <summary>Potentially life-threatening; act now.</summary>
    Critical,
}

/// <summary>An out-of-range vital sign. <see cref="Parameter"/> is the camelCase field name used by the UI.</summary>
public sealed record VitalFlag(string Parameter, decimal Value, FlagDirection Direction, FlagSeverity Severity, string Message);

/// <summary>Normalised reading passed to the assessor (temperature always in °C, glucose in mg/dL).</summary>
public sealed record VitalReading(
    decimal? TemperatureCelsius,
    int? Pulse,
    int? RespiratoryRate,
    int? SystolicBp,
    int? DiastolicBp,
    int? SpO2,
    bool OnSupplementalOxygen,
    decimal? Bmi,
    int? PainScore,
    decimal? BloodGlucose,
    GlucoseTiming GlucoseTiming,
    int? Gcs);

/// <summary>
/// Pure, age-aware abnormal-range flagging for vital signs. Thresholds follow widely used adult early-warning
/// bands (NEWS2-aligned for pulse, respiration and SpO₂), paediatric (PALS) heart-rate/respiratory-rate bands by age,
/// ADA glucose cut-offs and the Asian-Indian BMI cut-offs (overweight ≥ 23, obese ≥ 25). Flags support clinical
/// judgement — they never replace it — so every flag carries a plain-language message for the banner.
/// </summary>
public static class VitalSignsAssessor
{
    private sealed record Band(decimal NormalMin, decimal NormalMax, decimal? CriticalBelow, decimal? CriticalFrom);

    private enum AgeGroup
    {
        Neonate,
        Infant,
        Toddler,
        Preschool,
        School,
        Adolescent,
        Adult,
    }

    private static readonly Band Temperature = new(36.0m, 37.5m, 35.0m, 40.0m);
    private static readonly Band SpO2 = new(95m, 100m, 90m, null);
    private static readonly Band AdultSystolic = new(90m, 139m, 80m, 180m);
    private static readonly Band AdultDiastolic = new(60m, 89m, 40m, 120m);
    private static readonly Band Gcs = new(15m, 15m, 9m, null);

    /// <summary>Returns the flags for <paramref name="reading"/>; an empty list means every recorded value is in range.</summary>
    public static IReadOnlyList<VitalFlag> Assess(VitalReading reading, DateOnly? dateOfBirth, DateOnly today)
    {
        var group = GroupFor(dateOfBirth, today);
        var ageYears = dateOfBirth is { } dob ? Age.InYears(dob, today) : (int?)null;
        var flags = new List<VitalFlag>();

        if (reading.TemperatureCelsius is { } temperature)
        {
            Add(flags, "temperature", temperature, Temperature, (direction, critical) => direction == FlagDirection.High
                ? critical ? "Hyperpyrexia (≥ 40 °C)" : temperature >= 38.0m ? "Fever" : "Raised temperature"
                : critical ? "Hypothermia (< 35 °C)" : "Low temperature");
        }

        if (reading.Pulse is { } pulse)
        {
            Add(flags, "pulse", pulse, PulseBand(group), (direction, critical) =>
                (critical ? "Critical " : string.Empty) + (direction == FlagDirection.High ? "tachycardia" : "bradycardia") + AgeNote(group));
        }

        if (reading.RespiratoryRate is { } respiration)
        {
            Add(flags, "respiratoryRate", respiration, RespirationBand(group), (direction, critical) =>
                (critical ? "Critical " : string.Empty) + (direction == FlagDirection.High ? "tachypnoea" : "bradypnoea") + AgeNote(group));
        }

        AssessBloodPressure(flags, reading, group, ageYears);

        if (reading.SpO2 is { } saturation)
        {
            Add(flags, "spo2", saturation, SpO2, (_, critical) => critical ? "Severe hypoxaemia (SpO₂ < 90%)" : "Low oxygen saturation");
            if (reading.OnSupplementalOxygen)
            {
                flags.Add(new VitalFlag("spo2", saturation, FlagDirection.Low, FlagSeverity.Abnormal, "Patient is on supplemental oxygen"));
            }
        }

        if (reading.Bmi is { } bmi && ageYears is null or >= 18)
        {
            AssessBmi(flags, bmi);
        }

        if (reading.PainScore is { } pain && pain >= 4)
        {
            flags.Add(new VitalFlag("painScore", pain, FlagDirection.High, FlagSeverity.Abnormal, pain >= 7 ? "Severe pain (7–10)" : "Moderate pain (4–6)"));
        }

        if (reading.BloodGlucose is { } glucose)
        {
            AssessGlucose(flags, glucose, reading.GlucoseTiming);
        }

        if (reading.Gcs is { } gcs)
        {
            Add(flags, "gcs", gcs, Gcs, (_, critical) => critical ? "GCS ≤ 8 — severe impairment, airway at risk" : gcs >= 13 ? "GCS 13–14 — mild impairment" : "GCS 9–12 — moderate impairment");
        }

        return flags;
    }

    private static void AssessBloodPressure(List<VitalFlag> flags, VitalReading reading, AgeGroup group, int? ageYears)
    {
        // From 13 years adult cut-offs apply (AAP 2017). Younger children need height centiles for hypertension, so only
        // PALS hypotension (the dangerous direction) is flagged here.
        if (ageYears is null or >= 13)
        {
            if (reading.SystolicBp is { } systolic)
            {
                Add(flags, "systolicBp", systolic, AdultSystolic, (direction, critical) => direction == FlagDirection.High
                    ? critical ? "Hypertensive crisis range (≥ 180 systolic)" : "High systolic blood pressure"
                    : critical ? "Severe hypotension (< 80 systolic)" : "Low systolic blood pressure");
            }

            if (reading.DiastolicBp is { } diastolic)
            {
                Add(flags, "diastolicBp", diastolic, AdultDiastolic, (direction, critical) => direction == FlagDirection.High
                    ? critical ? "Hypertensive crisis range (≥ 120 diastolic)" : "High diastolic blood pressure"
                    : critical ? "Very low diastolic blood pressure" : "Low diastolic blood pressure");
            }

            return;
        }

        if (reading.SystolicBp is { } childSystolic)
        {
            var minimum = group switch
            {
                AgeGroup.Neonate => 60m,
                AgeGroup.Infant => 70m,
                _ => ageYears <= 10 ? 70m + (2m * ageYears.Value) : 90m,
            };
            if (childSystolic < minimum)
            {
                flags.Add(new VitalFlag("systolicBp", childSystolic, FlagDirection.Low, FlagSeverity.Critical,
                    $"Hypotension for age (systolic below {minimum:0})"));
            }
        }
    }

    private static void AssessBmi(List<VitalFlag> flags, decimal bmi)
    {
        var flag = bmi switch
        {
            < 16m => new VitalFlag("bmi", bmi, FlagDirection.Low, FlagSeverity.Abnormal, "Severe thinness (BMI < 16)"),
            < 18.5m => new VitalFlag("bmi", bmi, FlagDirection.Low, FlagSeverity.Abnormal, "Underweight (BMI < 18.5)"),
            >= 25m => new VitalFlag("bmi", bmi, FlagDirection.High, FlagSeverity.Abnormal, "Obese (Asian-Indian cut-off, BMI ≥ 25)"),
            >= 23m => new VitalFlag("bmi", bmi, FlagDirection.High, FlagSeverity.Abnormal, "Overweight (Asian-Indian cut-off, BMI ≥ 23)"),
            _ => null,
        };
        if (flag is not null)
        {
            flags.Add(flag);
        }
    }

    private static void AssessGlucose(List<VitalFlag> flags, decimal glucose, GlucoseTiming timing)
    {
        var fasting = timing == GlucoseTiming.Fasting;
        var normalMax = fasting ? 99m : 139m;
        var flag = glucose switch
        {
            < 54m => new VitalFlag("bloodGlucose", glucose, FlagDirection.Low, FlagSeverity.Critical, "Hypoglycaemia level 2 (< 54 mg/dL)"),
            < 70m => new VitalFlag("bloodGlucose", glucose, FlagDirection.Low, FlagSeverity.Abnormal, "Hypoglycaemia level 1 (54–69 mg/dL)"),
            >= 400m => new VitalFlag("bloodGlucose", glucose, FlagDirection.High, FlagSeverity.Critical, "Severe hyperglycaemia (≥ 400 mg/dL)"),
            _ when glucose <= normalMax => null,
            _ when fasting => new VitalFlag("bloodGlucose", glucose, FlagDirection.High, FlagSeverity.Abnormal,
                glucose >= 126m ? "Fasting glucose in the diabetic range (≥ 126 mg/dL)" : "Impaired fasting glucose (100–125 mg/dL)"),
            _ => new VitalFlag("bloodGlucose", glucose, FlagDirection.High, FlagSeverity.Abnormal,
                glucose >= 200m ? "Glucose in the diabetic range (≥ 200 mg/dL)" : "Glucose above normal (≥ 140 mg/dL)"),
        };
        if (flag is not null)
        {
            flags.Add(flag);
        }
    }

    private static void Add(List<VitalFlag> flags, string parameter, decimal value, Band band, Func<FlagDirection, bool, string> message)
    {
        if (band.CriticalBelow is { } criticalLow && value < criticalLow)
        {
            flags.Add(new VitalFlag(parameter, value, FlagDirection.Low, FlagSeverity.Critical, message(FlagDirection.Low, true)));
        }
        else if (value < band.NormalMin)
        {
            flags.Add(new VitalFlag(parameter, value, FlagDirection.Low, FlagSeverity.Abnormal, message(FlagDirection.Low, false)));
        }
        else if (band.CriticalFrom is { } criticalHigh && value >= criticalHigh)
        {
            flags.Add(new VitalFlag(parameter, value, FlagDirection.High, FlagSeverity.Critical, message(FlagDirection.High, true)));
        }
        else if (value > band.NormalMax)
        {
            flags.Add(new VitalFlag(parameter, value, FlagDirection.High, FlagSeverity.Abnormal, message(FlagDirection.High, false)));
        }
    }

    private static AgeGroup GroupFor(DateOnly? dateOfBirth, DateOnly today)
    {
        if (dateOfBirth is not { } dob)
        {
            return AgeGroup.Adult;
        }

        var days = Age.InDays(dob, today);
        var years = Age.InYears(dob, today);
        return days switch
        {
            <= 28 => AgeGroup.Neonate,
            _ when years < 1 => AgeGroup.Infant,
            _ when years < 3 => AgeGroup.Toddler,
            _ when years < 6 => AgeGroup.Preschool,
            _ when years < 12 => AgeGroup.School,
            _ when years < 18 => AgeGroup.Adolescent,
            _ => AgeGroup.Adult,
        };
    }

    private static Band PulseBand(AgeGroup group) => group switch
    {
        AgeGroup.Neonate => new Band(100m, 180m, 80m, 220m),
        AgeGroup.Infant => new Band(100m, 160m, 80m, 200m),
        AgeGroup.Toddler => new Band(98m, 140m, 70m, 180m),
        AgeGroup.Preschool => new Band(80m, 120m, 60m, 160m),
        AgeGroup.School => new Band(75m, 118m, 55m, 150m),
        _ => new Band(60m, 100m, 41m, 131m),
    };

    private static Band RespirationBand(AgeGroup group) => group switch
    {
        AgeGroup.Neonate => new Band(30m, 60m, 20m, 70m),
        AgeGroup.Infant => new Band(30m, 53m, 20m, 65m),
        AgeGroup.Toddler => new Band(22m, 37m, 15m, 50m),
        AgeGroup.Preschool => new Band(20m, 28m, 12m, 40m),
        AgeGroup.School => new Band(18m, 25m, 10m, 35m),
        _ => new Band(12m, 20m, 9m, 25m),
    };

    private static string AgeNote(AgeGroup group) => group switch
    {
        AgeGroup.Adult or AgeGroup.Adolescent => string.Empty,
        _ => " for age",
    };
}
