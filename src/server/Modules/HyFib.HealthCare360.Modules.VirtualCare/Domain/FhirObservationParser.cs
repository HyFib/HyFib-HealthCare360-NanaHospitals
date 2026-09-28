using System.Globalization;
using System.Text.Json;

namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

/// <summary>A reading extracted from a device payload, before unit normalisation and threshold evaluation.</summary>
public sealed record ReadingInput(RpmReadingType Type, decimal Value, string? Unit, DateTimeOffset? MeasuredAt, string? TextValue = null);

public sealed record FhirParseResult(IReadOnlyList<ReadingInput> Readings, IReadOnlyList<string> Errors);

/// <summary>
/// Reads the minimal HL7 FHIR R4 Observation shape that home-monitoring gateways send (RPM-02): a single Observation or
/// a Bundle of them, identified by LOINC codes, with <c>valueQuantity</c> (or <c>component</c>s for a blood-pressure
/// panel, or a coded interpretation for an ECG rhythm) and <c>effectiveDateTime</c>. Parsed with System.Text.Json so
/// no FHIR SDK is needed; anything unrecognised is reported back instead of being silently dropped.
/// </summary>
public static class FhirObservationParser
{
    public const int MaxEntries = 500;

    private static readonly Dictionary<string, RpmReadingType> Loinc = new(StringComparer.Ordinal)
    {
        ["8480-6"] = RpmReadingType.SystolicBp,
        ["8462-4"] = RpmReadingType.DiastolicBp,
        ["8867-4"] = RpmReadingType.HeartRate,
        ["59408-5"] = RpmReadingType.SpO2,
        ["2708-6"] = RpmReadingType.SpO2,
        ["2339-0"] = RpmReadingType.Glucose,
        ["2345-7"] = RpmReadingType.Glucose,
        ["41653-7"] = RpmReadingType.Glucose,
        ["15074-8"] = RpmReadingType.Glucose,
        ["29463-7"] = RpmReadingType.Weight,
        ["3141-9"] = RpmReadingType.Weight,
        ["8310-5"] = RpmReadingType.Temperature,
        ["8331-1"] = RpmReadingType.Temperature,
        ["8601-7"] = RpmReadingType.EcgRhythm,
        ["8884-9"] = RpmReadingType.EcgRhythm,
    };

    /// <summary>Blood-pressure panels whose values are in components.</summary>
    private static readonly HashSet<string> Panels = new(StringComparer.Ordinal) { "85354-9", "55284-4", "35094-2" };

    public static bool LooksLikeFhir(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("resourceType", out var type) && type.ValueKind == JsonValueKind.String;

    public static FhirParseResult Parse(JsonElement body)
    {
        var readings = new List<ReadingInput>();
        var errors = new List<string>();
        switch (ResourceType(body))
        {
            case "Observation":
                ParseObservation(body, "Observation", readings, errors);
                break;

            case "Bundle" when body.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array:
                var index = 0;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (index == MaxEntries)
                    {
                        errors.Add($"Only the first {MaxEntries} entries were read.");
                        break;
                    }

                    var where = $"entry[{index++}]";
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("resource", out var resource) && ResourceType(resource) == "Observation")
                    {
                        ParseObservation(resource, where, readings, errors);
                    }
                    else
                    {
                        errors.Add($"{where}: only Observation resources are accepted.");
                    }
                }

                break;

            default:
                errors.Add("Send a FHIR Observation or a Bundle of Observations.");
                break;
        }

        return new FhirParseResult(readings, errors);
    }

    private static void ParseObservation(JsonElement observation, string where, List<ReadingInput> readings, List<string> errors)
    {
        if (ReadString(observation, "status") is "entered-in-error" or "cancelled")
        {
            return;
        }

        var measuredAt = EffectiveInstant(observation);
        var codes = Codes(observation);
        var before = readings.Count;

        if (!codes.Any(Panels.Contains))
        {
            var type = TypeOf(codes);
            if (type is null)
            {
                if (!HasComponents(observation))
                {
                    errors.Add($"{where}: unsupported observation code ({string.Join(", ", codes)}).");
                    return;
                }
            }
            else if (TryQuantity(observation, "valueQuantity", out var value, out var unit))
            {
                readings.Add(new ReadingInput(type.Value, value, unit, measuredAt));
            }
            else if (type == RpmReadingType.EcgRhythm && TryText(observation, out var interpretation))
            {
                readings.Add(new ReadingInput(type.Value, ReadingUnits.ClassifyRhythm(interpretation), null, measuredAt, interpretation));
            }
            else if (!HasComponents(observation))
            {
                errors.Add($"{where}: the observation has no numeric value.");
                return;
            }
        }

        // Panels (blood pressure) and multi-value observations (SpO2 with pulse rate) carry values in components.
        if (observation.TryGetProperty("component", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in components.EnumerateArray())
            {
                if (TypeOf(Codes(component)) is { } componentType && TryQuantity(component, "valueQuantity", out var value, out var unit))
                {
                    readings.Add(new ReadingInput(componentType, value, unit, measuredAt));
                }
            }
        }

        if (readings.Count == before)
        {
            errors.Add($"{where}: no supported values were found.");
        }
    }

    private static RpmReadingType? TypeOf(IEnumerable<string> codes)
    {
        foreach (var code in codes)
        {
            if (Loinc.TryGetValue(code, out var type))
            {
                return type;
            }
        }

        return null;
    }

    private static List<string> Codes(JsonElement element)
    {
        var codes = new List<string>();
        if (element.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Object
            && code.TryGetProperty("coding", out var coding) && coding.ValueKind == JsonValueKind.Array)
        {
            codes.AddRange(coding.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.Object)
                .Select(c => ReadString(c, "code"))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!.Trim()));
        }

        return codes;
    }

    private static bool TryQuantity(JsonElement element, string property, out decimal value, out string? unit)
    {
        value = 0;
        unit = null;
        if (!element.TryGetProperty(property, out var quantity) || quantity.ValueKind != JsonValueKind.Object
            || !quantity.TryGetProperty("value", out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetDecimal(out value))
        {
            return false;
        }

        unit = ReadString(quantity, "unit") ?? ReadString(quantity, "code");
        return true;
    }

    private static bool TryText(JsonElement observation, out string text)
    {
        text = string.Empty;
        if (observation.TryGetProperty("valueCodeableConcept", out var concept) && concept.ValueKind == JsonValueKind.Object)
        {
            var display = ReadString(concept, "text");
            if (display is null && concept.TryGetProperty("coding", out var coding) && coding.ValueKind == JsonValueKind.Array)
            {
                display = coding.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object).Select(c => ReadString(c, "display")).FirstOrDefault(d => d is not null);
            }

            text = display ?? string.Empty;
        }
        else
        {
            text = ReadString(observation, "valueString") ?? string.Empty;
        }

        text = text.Trim();
        if (text.Length > 200)
        {
            text = text[..200];
        }

        return text.Length > 0;
    }

    private static DateTimeOffset? EffectiveInstant(JsonElement observation)
    {
        var raw = ReadString(observation, "effectiveDateTime") ?? ReadString(observation, "effectiveInstant") ?? ReadString(observation, "issued");
        if (raw is null && observation.TryGetProperty("effectivePeriod", out var period) && period.ValueKind == JsonValueKind.Object)
        {
            raw = ReadString(period, "start");
        }

        return raw is not null && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant)
            ? instant
            : null;
    }

    private static bool HasComponents(JsonElement observation) =>
        observation.TryGetProperty("component", out var components) && components.ValueKind == JsonValueKind.Array && components.GetArrayLength() > 0;

    private static string? ResourceType(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? ReadString(element, "resourceType") : null;

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
