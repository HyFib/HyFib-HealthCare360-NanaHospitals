using System.Globalization;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>A result as it will be printed, with its interpretation against the patient's reference interval.</summary>
public sealed record InterpretedResult(string ValueText, decimal? NumericValue, ResultFlag? Flag, string? ReferenceText);

/// <summary>
/// Parses and flags a raw result (pure; unit-tested). Numeric results accept analyser comparators ("&lt;0.005",
/// "&gt;600") so values beyond the measuring range are still flagged. Flags are computed on the unrounded value: a
/// potassium of 6.54 is critical against a limit of 6.5 even though it prints as "6.5".
/// </summary>
internal static class ResultInterpreter
{
    public const int MaxTextLength = 2000;

    /// <summary>Stored precision of numeric values (the column is numeric(18,4)).</summary>
    private const int StoredDecimals = 4;

    private static readonly string[] Comparators = ["<=", ">=", "<", ">"];

    public static Result<InterpretedResult> Interpret(LabTest test, ReferenceRange? range, string? rawValue, string? organism = null)
    {
        var raw = rawValue?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return Error.Field("value", $"Enter a result for {test.Name}.");
        }

        if (raw.Length > MaxTextLength)
        {
            return Error.Field("value", $"The result for {test.Name} is too long ({MaxTextLength} characters at most).");
        }

        var reference = ReferenceText(range, test.Decimals);
        switch (test.ResultType)
        {
            case LabResultType.Numeric:
                return InterpretNumeric(test, range, raw, reference);

            case LabResultType.Options:
                var chosen = test.Options.Count == 0
                    ? raw
                    : test.Options.FirstOrDefault(o => string.Equals(o, raw, StringComparison.OrdinalIgnoreCase));
                return chosen is null
                    ? Error.Field("value", $"Choose one of: {string.Join(", ", test.Options)}.")
                    : new InterpretedResult(chosen, null, TextFlag(range, chosen), reference);

            case LabResultType.Culture:
                return new InterpretedResult(raw, null, CultureFlag(range, raw, organism), reference);

            default:
                return new InterpretedResult(raw, null, TextFlag(range, raw), reference);
        }
    }

    /// <summary>Accepts "12.5", "1,20,000", "&lt;0.01", "≥ 600" and "-2.5"; commas are digit grouping (Indian style).</summary>
    internal static bool TryParseNumeric(string raw, out string? comparator, out decimal value)
    {
        comparator = null;
        var text = raw.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("≤", "<=", StringComparison.Ordinal)
            .Replace("≥", ">=", StringComparison.Ordinal);

        foreach (var candidate in Comparators)
        {
            if (text.StartsWith(candidate, StringComparison.Ordinal))
            {
                comparator = candidate;
                text = text[candidate.Length..];
                break;
            }
        }

        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Flag for an exact numeric value. Limits are exclusive: a value equal to a limit is inside the interval.</summary>
    internal static ResultFlag? Classify(ReferenceRange range, decimal value)
    {
        if (range.CriticalLow is { } criticalLow && value < criticalLow)
        {
            return ResultFlag.CriticalLow;
        }

        if (range.CriticalHigh is { } criticalHigh && value > criticalHigh)
        {
            return ResultFlag.CriticalHigh;
        }

        if (range.Low is { } low && value < low)
        {
            return ResultFlag.Low;
        }

        if (range.High is { } high && value > high)
        {
            return ResultFlag.High;
        }

        return HasNumericLimits(range) ? ResultFlag.Normal : null;
    }

    /// <summary>
    /// "&lt;x": the true value may be anywhere below x. If the interval has a critical low, it is treated as possibly
    /// critical, which forces a documented review. It is Normal only when x itself is within an interval that has no lower
    /// limit. Otherwise the flag is left undetermined for manual review.
    /// </summary>
    internal static ResultFlag? ClassifyBelow(ReferenceRange range, decimal bound)
    {
        if (range.CriticalLow is not null)
        {
            return ResultFlag.CriticalLow;
        }

        if (range.Low is not null)
        {
            return ResultFlag.Low;
        }

        var ceiling = range.High ?? range.CriticalHigh;
        return ceiling is { } limit && bound <= limit ? ResultFlag.Normal : null;
    }

    /// <summary>"&gt;x": mirror image of <see cref="ClassifyBelow"/>.</summary>
    internal static ResultFlag? ClassifyAbove(ReferenceRange range, decimal bound)
    {
        if (range.CriticalHigh is not null)
        {
            return ResultFlag.CriticalHigh;
        }

        if (range.High is not null)
        {
            return ResultFlag.High;
        }

        var floor = range.Low ?? range.CriticalLow;
        return floor is { } limit && bound >= limit ? ResultFlag.Normal : null;
    }

    /// <summary>The reference text printed beside the result ("13.0 - 17.0", "Up to 40", "Non-reactive").</summary>
    internal static string? ReferenceText(ReferenceRange? range, int decimals)
    {
        if (range is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(range.Text))
        {
            return range.Text.Trim();
        }

        return (range.Low, range.High) switch
        {
            ({ } low, { } high) => $"{Format(low, decimals)} - {Format(high, decimals)}",
            (null, { } high) => $"Up to {Format(high, decimals)}",
            ({ } low, null) => $"{Format(low, decimals)} and above",
            _ => range.NormalValues.Count > 0 ? string.Join(" / ", range.NormalValues) : null,
        };
    }

    internal static string Format(decimal value, int decimals) =>
        Math.Round(value, Math.Clamp(decimals, 0, 6), MidpointRounding.AwayFromZero)
            .ToString("F" + Math.Clamp(decimals, 0, 6).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static Result<InterpretedResult> InterpretNumeric(LabTest test, ReferenceRange? range, string raw, string? reference)
    {
        if (!TryParseNumeric(raw, out var comparator, out var number))
        {
            return Error.Field("value", $"{test.Name} needs a number (for example 12.5, <0.01 or >600).");
        }

        var flag = range is null
            ? null
            : comparator switch
            {
                null => Classify(range, number),
                "<" or "<=" => ClassifyBelow(range, number),
                _ => ClassifyAbove(range, number),
            };

        var stored = Math.Round(number, StoredDecimals, MidpointRounding.AwayFromZero);
        return new InterpretedResult(comparator + Format(number, test.Decimals), stored, flag, reference);
    }

    private static ResultFlag? TextFlag(ReferenceRange? range, string value)
    {
        if (range is null)
        {
            return null;
        }

        if (range.CriticalValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return ResultFlag.Critical;
        }

        if (range.NormalValues.Count == 0)
        {
            return null;
        }

        return range.NormalValues.Contains(value, StringComparer.OrdinalIgnoreCase) ? ResultFlag.Normal : ResultFlag.Abnormal;
    }

    /// <summary>An isolated organism is always abnormal; listed organisms/growth statements can be critical (e.g. blood culture).</summary>
    private static ResultFlag? CultureFlag(ReferenceRange? range, string value, string? organism)
    {
        var hasOrganism = !string.IsNullOrWhiteSpace(organism);
        if (range is not null && hasOrganism && range.CriticalValues.Contains(organism!.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return ResultFlag.Critical;
        }

        var flag = TextFlag(range, value);
        return hasOrganism && flag is null or ResultFlag.Normal ? ResultFlag.Abnormal : flag;
    }

    private static bool HasNumericLimits(ReferenceRange range) =>
        range.Low is not null || range.High is not null || range.CriticalLow is not null || range.CriticalHigh is not null;
}
