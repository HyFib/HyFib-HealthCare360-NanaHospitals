using System.Globalization;
using System.Text.Json;

namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

/// <summary>Readings found in a device request, with problems reported per line.</summary>
public sealed record DevicePayload(IReadOnlyList<ReadingInput> Readings, IReadOnlyList<string> Errors, ReadingSource Source);

/// <summary>
/// Accepts what real home-monitoring gateways send (RPM-02): a FHIR Observation/Bundle, or the simple HC360 format
/// <c>{"readings":[{"type":"SystolicBp","value":128,"unit":"mmHg","measuredAt":"2026-09-26T08:15:00+05:30"}]}</c>.
/// Reading types are matched leniently ("sbp", "pulse", "SpO2"…), a blood-pressure item may carry systolic, diastolic
/// and pulse together, and an ECG item may carry only an interpretation text.
/// </summary>
public static class DeviceReadingParser
{
    public const int MaxItems = 500;

    private static readonly Dictionary<string, RpmReadingType> Aliases = new(StringComparer.Ordinal)
    {
        ["systolicbp"] = RpmReadingType.SystolicBp,
        ["systolic"] = RpmReadingType.SystolicBp,
        ["sbp"] = RpmReadingType.SystolicBp,
        ["diastolicbp"] = RpmReadingType.DiastolicBp,
        ["diastolic"] = RpmReadingType.DiastolicBp,
        ["dbp"] = RpmReadingType.DiastolicBp,
        ["heartrate"] = RpmReadingType.HeartRate,
        ["pulse"] = RpmReadingType.HeartRate,
        ["pulserate"] = RpmReadingType.HeartRate,
        ["hr"] = RpmReadingType.HeartRate,
        ["spo2"] = RpmReadingType.SpO2,
        ["oxygensaturation"] = RpmReadingType.SpO2,
        ["glucose"] = RpmReadingType.Glucose,
        ["bloodglucose"] = RpmReadingType.Glucose,
        ["bloodsugar"] = RpmReadingType.Glucose,
        ["weight"] = RpmReadingType.Weight,
        ["bodyweight"] = RpmReadingType.Weight,
        ["temperature"] = RpmReadingType.Temperature,
        ["temp"] = RpmReadingType.Temperature,
        ["bodytemperature"] = RpmReadingType.Temperature,
        ["ecgrhythm"] = RpmReadingType.EcgRhythm,
        ["ecg"] = RpmReadingType.EcgRhythm,
        ["rhythm"] = RpmReadingType.EcgRhythm,
    };

    public static DevicePayload Parse(JsonElement body)
    {
        if (FhirObservationParser.LooksLikeFhir(body))
        {
            var fhir = FhirObservationParser.Parse(body);
            return new DevicePayload(fhir.Readings, fhir.Errors, ReadingSource.Fhir);
        }

        JsonElement items;
        if (body.ValueKind == JsonValueKind.Array)
        {
            items = body;
        }
        else if (body.ValueKind == JsonValueKind.Object && TryGet(body, "readings", out var readings) && readings.ValueKind == JsonValueKind.Array)
        {
            items = readings;
        }
        else
        {
            return new DevicePayload([], ["Send {\"readings\": [...]} or a FHIR Observation/Bundle."], ReadingSource.Device);
        }

        var result = new List<ReadingInput>();
        var errors = new List<string>();
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (index == MaxItems)
            {
                errors.Add($"Only the first {MaxItems} readings were read.");
                break;
            }

            ParseItem(item, $"#{++index}", result, errors);
        }

        return new DevicePayload(result, errors, ReadingSource.Device);
    }

    public static RpmReadingType? TypeOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var key = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return Aliases.TryGetValue(key, out var type) ? type : null;
    }

    private static void ParseItem(JsonElement item, string line, List<ReadingInput> readings, List<string> errors)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{line}: each reading must be an object.");
            return;
        }

        var typeName = ReadString(item, "type");
        var measuredAt = ReadInstant(item, "measuredAt") ?? ReadInstant(item, "timestamp");
        var unit = ReadString(item, "unit");
        var normalized = new string((typeName ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        // A blood-pressure item may carry the whole cuff result.
        if (normalized is "bloodpressure" or "bp" or "nibp")
        {
            var added = 0;
            foreach (var (field, type) in new[] { ("systolic", RpmReadingType.SystolicBp), ("diastolic", RpmReadingType.DiastolicBp), ("pulse", RpmReadingType.HeartRate) })
            {
                if (ReadNumber(item, field) is { } value)
                {
                    readings.Add(new ReadingInput(type, value, type == RpmReadingType.HeartRate ? null : unit, measuredAt));
                    added++;
                }
            }

            if (added == 0)
            {
                errors.Add($"{line}: a blood-pressure reading needs systolic and diastolic values.");
            }

            return;
        }

        if (TypeOf(typeName) is not { } readingType)
        {
            errors.Add($"{line}: unknown reading type '{typeName}'.");
            return;
        }

        var text = ReadString(item, "text") ?? ReadString(item, "interpretation");
        if (ReadNumber(item, "value") is { } number)
        {
            readings.Add(new ReadingInput(readingType, number, unit, measuredAt, text));
        }
        else if (readingType == RpmReadingType.EcgRhythm && !string.IsNullOrWhiteSpace(text))
        {
            readings.Add(new ReadingInput(readingType, ReadingUnits.ClassifyRhythm(text), null, measuredAt, text));
        }
        else
        {
            errors.Add($"{line}: a numeric value is required.");
        }
    }

    private static bool TryGet(JsonElement item, string name, out JsonElement value)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement item, string name) =>
        TryGet(item, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal? ReadNumber(JsonElement item, string name)
    {
        if (!TryGet(item, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static DateTimeOffset? ReadInstant(JsonElement item, string name) =>
        ReadString(item, name) is { } raw
        && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : null;
}
