using System.Globalization;

namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

public enum RpmReadingType
{
    SystolicBp = 0,
    DiastolicBp,
    HeartRate,
    SpO2,
    Glucose,
    Weight,
    Temperature,

    /// <summary>Rhythm classification from an ECG patch: 0 normal sinus rhythm, 1 other/inconclusive, 2 atrial fibrillation or worse.</summary>
    EcgRhythm,
}

public enum ReadingSource
{
    Device = 0,
    Fhir,

    /// <summary>Entered by a nurse (for example read out by the patient on a follow-up call).</summary>
    Manual,
}

public enum ReadingSeverity
{
    Normal = 0,
    Warning,
    Critical,
}

/// <summary>
/// One measurement (RPM-02). High-volume and append-only, so it is a plain <see cref="Entity"/> excluded from
/// data-change auditing (the table is a candidate for monthly partitioning, SCAL-04). Values are stored in canonical
/// units (mmHg, bpm, %, mg/dL, kg, °C) whatever the device sent.
/// </summary>
[NotAudited]
public sealed class RpmReading : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? DeviceId { get; set; }
    public RpmReadingType Type { get; set; }
    public decimal Value { get; set; }
    public string Unit { get; set; } = string.Empty;

    /// <summary>Device interpretation, for example "Atrial fibrillation suspected" from an ECG patch.</summary>
    public string? TextValue { get; set; }

    public DateTimeOffset MeasuredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public bool IsOutOfRange { get; set; }
    public ReadingSeverity Severity { get; set; }
    public ReadingSource Source { get; set; }
    public Guid? RecordedByUserId { get; set; }
}

/// <summary>
/// Canonical units, unit conversion and plausibility limits. Devices from different vendors report °F or °C,
/// mmol/L or mg/dL, lb or kg; converting at the door keeps thresholds and trend charts in one unit, and physically
/// impossible values (sensor faults, typing mistakes) are rejected instead of raising false alarms.
/// </summary>
public static class ReadingUnits
{
    public static string CanonicalUnit(RpmReadingType type) => type switch
    {
        RpmReadingType.SystolicBp or RpmReadingType.DiastolicBp => "mmHg",
        RpmReadingType.HeartRate => "bpm",
        RpmReadingType.SpO2 => "%",
        RpmReadingType.Glucose => "mg/dL",
        RpmReadingType.Weight => "kg",
        RpmReadingType.Temperature => "°C",
        _ => "class",
    };

    public static (decimal Min, decimal Max) PlausibleRange(RpmReadingType type) => type switch
    {
        RpmReadingType.SystolicBp => (50m, 300m),
        RpmReadingType.DiastolicBp => (20m, 200m),
        RpmReadingType.HeartRate => (20m, 300m),
        RpmReadingType.SpO2 => (50m, 100m),
        RpmReadingType.Glucose => (10m, 1000m),
        RpmReadingType.Weight => (0.5m, 400m),
        RpmReadingType.Temperature => (30m, 45m),
        _ => (0m, 9m),
    };

    /// <summary>Converts <paramref name="value"/> to the canonical unit and checks it is physiologically possible.</summary>
    public static Result<decimal> Normalize(RpmReadingType type, decimal value, string? unit)
    {
        var u = (unit ?? string.Empty).Trim().ToLowerInvariant();
        decimal? converted = type switch
        {
            RpmReadingType.SystolicBp or RpmReadingType.DiastolicBp => u switch
            {
                "" or "mmhg" or "mm[hg]" or "mm hg" => value,
                "kpa" => value * 7.50062m,
                _ => null,
            },
            RpmReadingType.HeartRate => u switch
            {
                "" or "bpm" or "/min" or "{beats}/min" or "beats/min" or "1/min" => value,
                _ => null,
            },
            RpmReadingType.SpO2 => u switch
            {
                "" or "%" => value,
                _ => null,
            },
            RpmReadingType.Glucose => u switch
            {
                "" or "mg/dl" => value,
                "mmol/l" => value * 18.016m,
                _ => null,
            },
            RpmReadingType.Weight => u switch
            {
                "" or "kg" => value,
                "g" => value / 1000m,
                "lb" or "lbs" or "[lb_av]" => value * 0.45359237m,
                _ => null,
            },
            RpmReadingType.Temperature => u switch
            {
                "" or "°c" or "c" or "cel" or "degc" or "celsius" => value,
                "°f" or "f" or "[degf]" or "degf" or "fahrenheit" => (value - 32m) * 5m / 9m,
                _ => null,
            },
            _ => value,
        };

        if (converted is not { } canonical)
        {
            return Error.Field("unit", $"'{unit}' is not a supported unit for {Label(type)} (use {CanonicalUnit(type)}).");
        }

        canonical = Math.Round(canonical, 2, MidpointRounding.AwayFromZero);
        var (min, max) = PlausibleRange(type);
        return canonical < min || canonical > max
            ? Error.Field("value", $"{Label(type)} {canonical.ToString("0.##", CultureInfo.InvariantCulture)} {CanonicalUnit(type)} is outside the possible range ({min.ToString("0.##", CultureInfo.InvariantCulture)}–{max.ToString("0.##", CultureInfo.InvariantCulture)}).")
            : canonical;
    }

    public static string Label(RpmReadingType type) => type switch
    {
        RpmReadingType.SystolicBp => "Systolic BP",
        RpmReadingType.DiastolicBp => "Diastolic BP",
        RpmReadingType.HeartRate => "Heart rate",
        RpmReadingType.SpO2 => "SpO2",
        RpmReadingType.Glucose => "Blood glucose",
        RpmReadingType.Weight => "Weight",
        RpmReadingType.Temperature => "Temperature",
        _ => "ECG rhythm",
    };

    public static string Describe(RpmReadingType type, decimal value, string? text = null) =>
        type == RpmReadingType.EcgRhythm && !string.IsNullOrWhiteSpace(text)
            ? $"{Label(type)}: {text.Trim()}"
            : $"{Label(type)} {value.ToString("0.##", CultureInfo.InvariantCulture)} {CanonicalUnit(type)}";

    /// <summary>
    /// Maps an ECG interpretation text to the rhythm class stored as the reading value: 0 normal sinus rhythm,
    /// 2 atrial fibrillation/flutter or ventricular tachycardia, 1 anything else (needs a look).
    /// </summary>
    public static decimal ClassifyRhythm(string? interpretation)
    {
        var text = (interpretation ?? string.Empty).ToLowerInvariant();
        if (text.Contains("fibrillation", StringComparison.Ordinal) || text.Contains("flutter", StringComparison.Ordinal)
            || text.Contains("afib", StringComparison.Ordinal)
            || (text.Contains("ventricular", StringComparison.Ordinal) && text.Contains("tachycardia", StringComparison.Ordinal)))
        {
            return 2m;
        }

        var sinus = text.Contains("sinus", StringComparison.Ordinal);
        var abnormalRate = text.Contains("brady", StringComparison.Ordinal) || text.Contains("tachy", StringComparison.Ordinal);
        return sinus && !abnormalRate && (text.Contains("normal", StringComparison.Ordinal) || text.Contains("rhythm", StringComparison.Ordinal)) ? 0m : 1m;
    }
}
