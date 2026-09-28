namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

/// <summary>
/// Limits for one reading type. Values inside [Min, Max] are normal; outside them a warning; beyond
/// [CriticalMin, CriticalMax] critical. Any bound may be absent (SpO2 has no upper limit).
/// </summary>
public readonly record struct ThresholdRange(decimal? Min, decimal? Max, decimal? CriticalMin = null, decimal? CriticalMax = null)
{
    public bool IsEmpty => Min is null && Max is null && CriticalMin is null && CriticalMax is null;

    /// <summary>Bounds must be ordered CriticalMin ≤ Min &lt; Max ≤ CriticalMax (for the ones present).</summary>
    public Result Validate()
    {
        if (IsEmpty)
        {
            return Error.Field("min", "Set at least one limit.");
        }

        if (Min is { } min && Max is { } max && min >= max)
        {
            return Error.Field("max", "The upper limit must be above the lower limit.");
        }

        decimal?[] ordered = [CriticalMin, Min, Max, CriticalMax];
        var present = ordered.Where(v => v is not null).Select(v => v!.Value).ToList();
        for (var i = 1; i < present.Count; i++)
        {
            if (present[i] < present[i - 1])
            {
                return Error.Field("criticalMax", "Critical limits must lie outside the normal range.");
            }
        }

        return Result.Success();
    }
}

/// <summary>Per-enrolment limits for one reading type (RPM-03); a clinician can tighten or relax them per patient.</summary>
public sealed class RpmThreshold : AuditableEntity
{
    public Guid EnrollmentId { get; set; }
    public RpmReadingType Type { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? CriticalMin { get; set; }
    public decimal? CriticalMax { get; set; }

    public ThresholdRange Range => new(Min, Max, CriticalMin, CriticalMax);

    public void Apply(ThresholdRange range)
    {
        Min = range.Min;
        Max = range.Max;
        CriticalMin = range.CriticalMin;
        CriticalMax = range.CriticalMax;
    }
}

/// <summary>Classifies a canonical reading against its limits (pure; unit-tested at every boundary).</summary>
public static class ThresholdEvaluator
{
    public static ReadingSeverity Evaluate(decimal value, ThresholdRange? range)
    {
        if (range is not { } r)
        {
            return ReadingSeverity.Normal;
        }

        if ((r.CriticalMin is { } criticalMin && value < criticalMin) || (r.CriticalMax is { } criticalMax && value > criticalMax))
        {
            return ReadingSeverity.Critical;
        }

        return (r.Min is { } min && value < min) || (r.Max is { } max && value > max)
            ? ReadingSeverity.Warning
            : ReadingSeverity.Normal;
    }
}

/// <summary>
/// Starting limits by programme (adults), based on common Indian and international guidance (ISH/ESC blood-pressure
/// bands, ADA/RSSDI glucose targets, SpO2 ≥ 94 %). They are copied into each enrolment and should be reviewed by the
/// treating doctor for every patient.
/// </summary>
public static class DefaultThresholds
{
    private static readonly ThresholdRange Systolic = new(90m, 140m, 80m, 180m);
    private static readonly ThresholdRange Diastolic = new(60m, 90m, 50m, 120m);
    private static readonly ThresholdRange Pulse = new(50m, 100m, 40m, 130m);
    private static readonly ThresholdRange Oxygen = new(94m, null, 90m, null);

    public static IReadOnlyDictionary<RpmReadingType, ThresholdRange> For(RpmProgramKind kind) => kind switch
    {
        RpmProgramKind.Hypertension => new Dictionary<RpmReadingType, ThresholdRange>
        {
            [RpmReadingType.SystolicBp] = Systolic,
            [RpmReadingType.DiastolicBp] = Diastolic,
            [RpmReadingType.HeartRate] = Pulse,
        },
        RpmProgramKind.Diabetes => new Dictionary<RpmReadingType, ThresholdRange>
        {
            [RpmReadingType.Glucose] = new(70m, 180m, 54m, 300m),
            [RpmReadingType.SystolicBp] = Systolic,
            [RpmReadingType.DiastolicBp] = Diastolic,
        },
        RpmProgramKind.Cardiac => new Dictionary<RpmReadingType, ThresholdRange>
        {
            [RpmReadingType.HeartRate] = Pulse,
            [RpmReadingType.SpO2] = Oxygen,
            [RpmReadingType.SystolicBp] = Systolic,
            [RpmReadingType.DiastolicBp] = Diastolic,
            [RpmReadingType.EcgRhythm] = new(null, 0m, null, 1m),
        },
        RpmProgramKind.PostDischarge => new Dictionary<RpmReadingType, ThresholdRange>
        {
            [RpmReadingType.Temperature] = new(36.1m, 37.8m, 35.0m, 39.0m),
            [RpmReadingType.SpO2] = Oxygen,
            [RpmReadingType.HeartRate] = new(50m, 110m, 40m, 130m),
            [RpmReadingType.SystolicBp] = new(90m, 150m, 80m, 180m),
        },
        _ => new Dictionary<RpmReadingType, ThresholdRange>(),
    };
}
