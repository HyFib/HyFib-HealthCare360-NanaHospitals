using System.Globalization;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Released laboratory results → FHIR <c>Observation</c> (category <c>laboratory</c>). The analyte is coded with its
/// LOINC code when the test master has one, plus the HC360 test code. Numeric results are quantities (UCUM when the unit
/// is known, analyser comparators such as "&lt;0.005" kept); other results are strings. The flag becomes the v3
/// interpretation and the printed reference interval the reference range. A result corrected after release is
/// <c>amended</c>. Only released tests are ever mapped.
/// </summary>
internal static partial class LabObservations
{
    public static R4.Observation Map(LabResultRow r)
    {
        var observation = new R4.Observation
        {
            Id = FhirIds.Of(FhirIds.Lab, r.Id),
            Meta = FhirElements.Meta(r.CreatedAt, r.UpdatedAt),
            Status = IsAmended(r) ? R4.ObservationStatus.Amended : R4.ObservationStatus.Final,
            Category = [VitalSignObservations.Category(VitalSignObservations.Laboratory)],
            Code = Code(r),
            Subject = FhirReferences.Patient(r.PatientId),
            Encounter = r.VisitId is { } visit ? FhirReferences.Encounter(FhirIds.Visit, visit) : null,
            Effective = new R4.FhirDateTime(FhirTime.DateTime(r.CollectedAt ?? r.OrderedAt)),
            Issued = r.ReleasedAt is { } released ? FhirTime.ToMilliseconds(released) : null,
            Method = FhirElements.Clean(r.Method) is { } method ? FhirElements.Text(method) : null,
        };

        if (Value(r) is { } value)
        {
            observation.Value = value;
        }
        else
        {
            observation.DataAbsentReason = FhirElements.Concept(FhirSystems.DataAbsentReason, "unknown", "Unknown");
        }

        if (r.Flag is { } flag)
        {
            observation.Interpretation.Add(Interpretation(flag));
        }

        if (ReferenceRange(r.ReferenceText, r.Unit) is { } range)
        {
            observation.ReferenceRange.Add(range);
        }

        foreach (var note in new[]
                 {
                     FhirElements.Clean(r.Organism) is { } organism ? $"Organism: {organism}" : null,
                     FhirElements.Clean(r.ColonyCount) is { } colonies ? $"Colony count: {colonies}" : null,
                     FhirElements.Clean(r.Remarks),
                 })
        {
            if (FhirElements.Note(note) is { } annotation)
            {
                observation.Note.Add(annotation);
            }
        }

        return observation;
    }

    /// <summary>Amended when the result was re-verified after its release (a numbered report amendment).</summary>
    public static bool IsAmended(LabResultRow r) => r is { VerifiedAt: { } verified, ReleasedAt: { } released } && verified > released;

    public static R4.CodeableConcept Code(LabResultRow r)
    {
        var concept = new R4.CodeableConcept { Text = r.TestName };
        if (FhirElements.Clean(r.Loinc) is { } loinc)
        {
            concept.Coding.Add(FhirElements.Coding(FhirSystems.Loinc, loinc));
        }

        if (FhirElements.Clean(r.TestCode) is { } code)
        {
            concept.Coding.Add(FhirElements.Coding(FhirSystems.LabTest, code, r.TestName));
        }

        return concept;
    }

    /// <summary>
    /// A numeric result as a quantity, parsed from the printed value so the reported precision is kept ("6.5", not the
    /// stored 6.5000); comparators and Indian digit grouping ("1,20,000") are understood. Other results are strings.
    /// </summary>
    public static R4.DataType? Value(LabResultRow r)
    {
        var text = FhirElements.Clean(r.ValueText);
        if (r.ResultType == LabResultType.Numeric)
        {
            if (TryParseNumber(text, out var comparator, out var number))
            {
                var quantity = Quantity(number, r.Unit);
                quantity.Comparator = comparator;
                return quantity;
            }

            if (r.NumericValue is { } stored)
            {
                return Quantity(stored, r.Unit);
            }
        }

        return text is null ? null : new R4.FhirString(text);
    }

    public static R4.Quantity Quantity(decimal value, string? unit)
    {
        var printed = FhirElements.Clean(unit);
        var ucum = UcumUnits.Code(printed);
        return new R4.Quantity
        {
            Value = FhirElements.Normalize(value),
            Unit = printed,
            System = ucum is null ? null : FhirSystems.Ucum,
            Code = ucum,
        };
    }

    public static bool TryParseNumber(string? text, out R4.Quantity.QuantityComparator? comparator, out decimal value)
    {
        comparator = null;
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("≤", "<=", StringComparison.Ordinal)
            .Replace("≥", ">=", StringComparison.Ordinal);
        foreach (var (symbol, kind) in Comparators)
        {
            if (compact.StartsWith(symbol, StringComparison.Ordinal))
            {
                comparator = kind;
                compact = compact[symbol.Length..];
                break;
            }
        }

        return decimal.TryParse(compact, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Result flag → v3 ObservationInterpretation (N, L, H, LL, HH, A, AA).</summary>
    public static R4.CodeableConcept Interpretation(ResultFlag flag) => flag switch
    {
        ResultFlag.Low => FhirElements.Concept(FhirSystems.ObservationInterpretation, "L", "Low"),
        ResultFlag.High => FhirElements.Concept(FhirSystems.ObservationInterpretation, "H", "High"),
        ResultFlag.CriticalLow => FhirElements.Concept(FhirSystems.ObservationInterpretation, "LL", "Critical low"),
        ResultFlag.CriticalHigh => FhirElements.Concept(FhirSystems.ObservationInterpretation, "HH", "Critical high"),
        ResultFlag.Abnormal => FhirElements.Concept(FhirSystems.ObservationInterpretation, "A", "Abnormal"),
        ResultFlag.Critical => FhirElements.Concept(FhirSystems.ObservationInterpretation, "AA", "Critical abnormal"),
        _ => FhirElements.Concept(FhirSystems.ObservationInterpretation, "N", "Normal"),
    };

    /// <summary>
    /// The printed reference interval ("13.0 - 17.0", "Up to 40", "40 and above", "&lt; 200") with low/high quantities
    /// when it is numeric; any other text (guideline bands, "Non-reactive") is kept as text only.
    /// </summary>
    public static R4.Observation.ReferenceRangeComponent? ReferenceRange(string? referenceText, string? unit)
    {
        if (FhirElements.Clean(referenceText) is not { } text)
        {
            return null;
        }

        var range = new R4.Observation.ReferenceRangeComponent { Text = text };
        var body = StripUnit(text, unit);
        if (Between().Match(body) is { Success: true } between)
        {
            range.Low = Quantity(Parse(between.Groups["low"].Value), unit);
            range.High = Quantity(Parse(between.Groups["high"].Value), unit);
        }
        else if (UpTo().Match(body) is { Success: true } upTo)
        {
            range.High = Quantity(Parse(upTo.Groups["high"].Value), unit);
        }
        else if (AndAbove().Match(body) is { Success: true } above)
        {
            range.Low = Quantity(Parse(above.Groups["low"].Value), unit);
        }

        return range;
    }

    private static string StripUnit(string text, string? unit) =>
        FhirElements.Clean(unit) is { } u && text.EndsWith(u, StringComparison.OrdinalIgnoreCase) ? text[..^u.Length].TrimEnd() : text;

    private static decimal Parse(string number) => decimal.Parse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    private static readonly (string Symbol, R4.Quantity.QuantityComparator Kind)[] Comparators =
    [
        ("<=", R4.Quantity.QuantityComparator.LessOrEqual),
        (">=", R4.Quantity.QuantityComparator.GreaterOrEqual),
        ("<", R4.Quantity.QuantityComparator.LessThan),
        (">", R4.Quantity.QuantityComparator.GreaterThan),
    ];

    [GeneratedRegex(@"^(?<low>\d+(\.\d+)?)\s*[-–]\s*(?<high>\d+(\.\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex Between();

    [GeneratedRegex(@"^(up\s*to|<=|≤|<)\s*(?<high>\d+(\.\d+)?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UpTo();

    [GeneratedRegex(@"^((?<low>\d+(\.\d+)?)\s*and\s+above|(>=|≥|>)\s*(?<low>\d+(\.\d+)?))$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AndAbove();
}
