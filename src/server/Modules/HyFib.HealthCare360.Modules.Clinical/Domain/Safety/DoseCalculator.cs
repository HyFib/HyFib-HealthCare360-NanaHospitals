using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

public enum AmountUnit
{
    /// <summary>Mass, normalised to milligrams.</summary>
    Mg = 0,

    /// <summary>Countable units: tablets, capsules, sachets…</summary>
    Count,
    Ml,

    /// <summary>Puffs, drops, IU and other units that cannot be converted to mg.</summary>
    Other,
}

public sealed record DoseAmount(decimal Value, AmountUnit Unit);

/// <summary>
/// Parses the dose, frequency and strength text doctors write ("1 tab", "500 mg", "½", "1-0-1", "8 hourly",
/// "400 mg + 325 mg", "250 mg/5 ml") into numbers for quantity calculation and the maximum-dose check.
/// Anything ambiguous returns null rather than guessing — the caller then asks for the value or reports that the
/// dose could not be verified.
/// </summary>
public static partial class DoseCalculator
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"^(?<num>\d+(?:\.\d+)?)(?:\s*/\s*(?<den>\d+))?\s*(?<unit>[a-zµ]+)?")]
    private static partial Regex DosePattern();

    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)(\s*-\s*\d+(?:\.\d+)?){1,3}\s*$")]
    private static partial Regex TimesOfDayPattern();

    [GeneratedRegex(@"(?:q\s*|every\s*)?(\d{1,2})\s*(?:st|nd|rd|th)?\s*(?:h|hr|hrs|hour|hours|hourly)\b")]
    private static partial Regex HourlyPattern();

    [GeneratedRegex(@"(?<num>\d+(?:\.\d+)?)\s*(?<unit>mg|mcg|µg|gm|g|iu|%)?")]
    private static partial Regex StrengthPart();

    [GeneratedRegex(@"(?<num>\d+(?:\.\d+)?)\s*(?<unit>mg|mcg|µg|gm|g)\s*(?:/|per)\s*(?<vol>\d+(?:\.\d+)?)?\s*ml")]
    private static partial Regex ConcentrationPart();

    public static DoseAmount? ParseDose(string? dose)
    {
        if (string.IsNullOrWhiteSpace(dose))
        {
            return null;
        }

        var text = ReplaceVulgarFractions(dose.Trim().ToLowerInvariant());
        var match = DosePattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = decimal.Parse(match.Groups["num"].Value, Invariant);
        if (match.Groups["den"].Success)
        {
            var denominator = decimal.Parse(match.Groups["den"].Value, Invariant);
            if (denominator == 0)
            {
                return null;
            }

            value /= denominator;
        }

        var unit = match.Groups["unit"].Success ? match.Groups["unit"].Value : string.Empty;
        return unit switch
        {
            "mg" => new DoseAmount(value, AmountUnit.Mg),
            "g" or "gm" or "gram" or "grams" => new DoseAmount(value * 1000m, AmountUnit.Mg),
            "mcg" or "µg" or "microgram" or "micrograms" => new DoseAmount(value / 1000m, AmountUnit.Mg),
            "ml" => new DoseAmount(value, AmountUnit.Ml),
            "" or "tab" or "tabs" or "tablet" or "tablets" or "cap" or "caps" or "capsule" or "capsules" or "sachet" or "sachets"
                or "supp" or "suppository" or "suppositories" or "lozenge" or "lozenges" or "no" or "nos" => new DoseAmount(value, AmountUnit.Count),
            _ => new DoseAmount(value, AmountUnit.Other),
        };
    }

    /// <summary>
    /// Administrations per day. For custom "1-0-1" patterns the numbers are doses at each time of day, so "2-0-2"
    /// means four doses a day. Returns null for SOS without an explicit maximum and for unreadable custom text.
    /// </summary>
    public static decimal? DosesPerDay(DoseFrequency frequency, string? customFrequency, decimal? explicitPerDay)
    {
        if (explicitPerDay is > 0)
        {
            return explicitPerDay;
        }

        return frequency switch
        {
            DoseFrequency.OD or DoseFrequency.HS or DoseFrequency.STAT => 1m,
            DoseFrequency.BD => 2m,
            DoseFrequency.TDS => 3m,
            DoseFrequency.QID => 4m,
            DoseFrequency.Weekly => 1m / 7m,
            DoseFrequency.Custom => ParseCustomFrequency(customFrequency),
            _ => null,
        };
    }

    public static decimal? ParseCustomFrequency(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = ReplaceVulgarFractions(text.Trim().ToLowerInvariant());
        if (TimesOfDayPattern().IsMatch(value))
        {
            var total = value.Split('-').Sum(part => decimal.Parse(part.Trim(), Invariant));
            return total > 0 ? total : null;
        }

        var hourly = HourlyPattern().Match(value);
        if (hourly.Success && int.TryParse(hourly.Groups[1].Value, NumberStyles.None, Invariant, out var hours) && hours is >= 1 and <= 24)
        {
            return 24m / hours;
        }

        return value switch
        {
            _ when value.Contains("alternate", StringComparison.Ordinal) || value.Contains("every other day", StringComparison.Ordinal) => 0.5m,
            _ when value.Contains("thrice weekly", StringComparison.Ordinal) || value.Contains("three times a week", StringComparison.Ordinal) => 3m / 7m,
            _ when value.Contains("twice weekly", StringComparison.Ordinal) || value.Contains("twice a week", StringComparison.Ordinal) => 2m / 7m,
            _ when value.Contains("week", StringComparison.Ordinal) => 1m / 7m,
            "once daily" or "once a day" or "daily" => 1m,
            "twice daily" or "twice a day" => 2m,
            "thrice daily" or "three times a day" => 3m,
            "four times a day" => 4m,
            _ => null,
        };
    }

    /// <summary>Per-unit strengths in mg, in label order ("400 mg + 325 mg" → 400, 325). Empty for liquids, IU or percentages.</summary>
    public static IReadOnlyList<decimal> ParseStrengthMg(string? strength)
    {
        if (string.IsNullOrWhiteSpace(strength))
        {
            return [];
        }

        var text = strength.ToLowerInvariant();
        if (ConcentrationPart().IsMatch(text) || text.Contains("ml", StringComparison.Ordinal))
        {
            return [];
        }

        var parts = StrengthPart().Matches(text)
            .Select(m => (Value: decimal.Parse(m.Groups["num"].Value, Invariant), Unit: m.Groups["unit"].Success ? m.Groups["unit"].Value : null))
            .ToList();
        if (parts.Count == 0 || parts.Any(p => p.Unit is "iu" or "%"))
        {
            return [];
        }

        // "400/325 mg": a trailing unit applies to the bare numbers before it; no unit at all means mg.
        var result = new List<decimal>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            var unit = parts[i].Unit ?? parts.Skip(i + 1).Select(p => p.Unit).FirstOrDefault(u => u is not null) ?? "mg";
            result.Add(ToMg(parts[i].Value, unit));
        }

        return result;
    }

    /// <summary>Concentrations in mg per ml ("250 mg/5 ml" → 50).</summary>
    public static IReadOnlyList<decimal> ParseConcentrationMgPerMl(string? strength)
    {
        if (string.IsNullOrWhiteSpace(strength))
        {
            return [];
        }

        return ConcentrationPart().Matches(strength.ToLowerInvariant())
            .Select(m =>
            {
                var mg = ToMg(decimal.Parse(m.Groups["num"].Value, Invariant), m.Groups["unit"].Value);
                var volume = m.Groups["vol"].Success ? decimal.Parse(m.Groups["vol"].Value, Invariant) : 1m;
                return volume > 0 ? mg / volume : 0m;
            })
            .Where(v => v > 0)
            .ToList();
    }

    /// <summary>
    /// Units to dispense for solid forms: units per dose × doses per day × days (weekly doses counted per week),
    /// rounded up to whole units. Null when it cannot be computed safely (liquids, SOS without a maximum…).
    /// </summary>
    public static decimal? QuantityToDispense(string dose, string? strength, DoseFrequency frequency, string? customFrequency, decimal? dosesPerDay, int durationDays)
    {
        var amount = ParseDose(dose);
        if (amount is null || durationDays <= 0)
        {
            return null;
        }

        decimal? unitsPerDose = amount.Unit switch
        {
            AmountUnit.Count => amount.Value,
            AmountUnit.Mg when ParseStrengthMg(strength) is [var unitMg] && unitMg > 0 && amount.Value % (unitMg / 2m) == 0 => amount.Value / unitMg,
            _ => null,
        };
        if (unitsPerDose is not > 0)
        {
            return null;
        }

        if (frequency == DoseFrequency.STAT)
        {
            return Math.Ceiling(unitsPerDose.Value);
        }

        if (frequency == DoseFrequency.Weekly && dosesPerDay is null)
        {
            return Math.Ceiling(Math.Ceiling(durationDays / 7m) * unitsPerDose.Value);
        }

        var perDay = DosesPerDay(frequency, customFrequency, dosesPerDay);
        return perDay is > 0 ? Math.Ceiling(unitsPerDose.Value * perDay.Value * durationDays) : null;
    }

    public static decimal ToMg(decimal value, string unit) => unit switch
    {
        "g" or "gm" => value * 1000m,
        "mcg" or "µg" => value / 1000m,
        _ => value,
    };

    private static string ReplaceVulgarFractions(string text) =>
        text.Replace("1½", "1.5", StringComparison.Ordinal)
            .Replace("½", "0.5", StringComparison.Ordinal)
            .Replace("¼", "0.25", StringComparison.Ordinal)
            .Replace("¾", "0.75", StringComparison.Ordinal);
}
