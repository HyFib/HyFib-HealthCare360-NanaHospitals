using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Platform.Numbering;

public enum NumberFormatPartKind
{
    /// <summary>Fixed text, written into every number as it is.</summary>
    Text,

    /// <summary>{FAC}: the facility code.</summary>
    FacilityCode,

    /// <summary>{YYYY}: the year, 4 digits.</summary>
    Year,

    /// <summary>{YY}: the year, 2 digits.</summary>
    ShortYear,

    /// <summary>{MM}: the month, 2 digits.</summary>
    Month,

    /// <summary>{DD}: the day of the month, 2 digits.</summary>
    Day,

    /// <summary>{SEQ} or {SEQ:n}: the running number, padded with zeros to n digits.</summary>
    Sequence,

    /// <summary>Something in braces that is none of the parts above.</summary>
    Unknown,

    /// <summary>A "{" without its "}" (to the end of the format).</summary>
    Unclosed,
}

/// <summary>A series' counter: the last number issued and the period it was issued in (null when it never restarts).</summary>
public readonly record struct NumberCounter(long Value, string? Period);

/// <summary>A piece of a number format.</summary>
/// <param name="Text">The characters as written; for a part in braces, with its braces.</param>
/// <param name="Width">For the running number, the digits it is padded to ({SEQ:6} → 6); null for {SEQ}.</param>
public readonly record struct NumberFormatPart(NumberFormatPartKind Kind, string Text, int? Width = null);

/// <summary>
/// Number formats as pure functions: the parts of <c>{FAC}OB{YY}{SEQ:7}</c>, the number it makes, and the period a
/// counter belongs to. Dates are the hospital's (India Standard Time).
/// </summary>
public static class NumberFormat
{
    private static readonly Lazy<TimeZoneInfo> HospitalZone = new(() =>
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    });

    /// <summary>The hospital's wall-clock time at <paramref name="instant"/>.</summary>
    public static DateTimeOffset ToHospitalTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, HospitalZone.Value);

    /// <summary>Splits a format into fixed text and parts in braces. Never fails: what is wrong comes back as a part.</summary>
    public static IReadOnlyList<NumberFormatPart> Parse(string format)
    {
        var parts = new List<NumberFormatPart>();
        var text = new StringBuilder();
        var index = 0;
        while (index < format.Length)
        {
            if (format[index] != '{')
            {
                text.Append(format[index]);
                index++;
                continue;
            }

            if (text.Length > 0)
            {
                parts.Add(new NumberFormatPart(NumberFormatPartKind.Text, text.ToString()));
                text.Clear();
            }

            var close = format.IndexOf('}', index + 1);
            if (close < 0)
            {
                parts.Add(new NumberFormatPart(NumberFormatPartKind.Unclosed, format[index..]));
                return parts;
            }

            parts.Add(Classify(format[index..(close + 1)]));
            index = close + 1;
        }

        if (text.Length > 0)
        {
            parts.Add(new NumberFormatPart(NumberFormatPartKind.Text, text.ToString()));
        }

        return parts;
    }

    /// <summary>
    /// The number <paramref name="format"/> makes for running number <paramref name="value"/>. Anything the format may
    /// not contain is written as it is, and the running number is padded to at most
    /// <see cref="NumberFormatRules.MaxSequenceWidth"/> digits: a stored format the rules now refuse, such as
    /// {SEQ:999999999} saved before they existed, still makes a number instead of exhausting the server's memory.
    /// </summary>
    public static string Render(string format, long value, string? facilityCode, DateTimeOffset localNow)
    {
        var number = new StringBuilder();
        foreach (var part in Parse(format))
        {
            number.Append(part.Kind switch
            {
                NumberFormatPartKind.FacilityCode => facilityCode ?? string.Empty,
                NumberFormatPartKind.Year => localNow.ToString("yyyy", CultureInfo.InvariantCulture),
                NumberFormatPartKind.ShortYear => localNow.ToString("yy", CultureInfo.InvariantCulture),
                NumberFormatPartKind.Month => localNow.ToString("MM", CultureInfo.InvariantCulture),
                NumberFormatPartKind.Day => localNow.ToString("dd", CultureInfo.InvariantCulture),
                NumberFormatPartKind.Sequence => value.ToString(CultureInfo.InvariantCulture)
                    .PadLeft(Math.Min(part.Width ?? 0, NumberFormatRules.MaxSequenceWidth), '0'),
                _ => part.Text,
            });
        }

        return number.ToString();
    }

    /// <summary>The period a counter with this reset counts in at <paramref name="localNow"/>; null when it never restarts.</summary>
    public static string? PeriodFor(NumberResetPolicy policy, DateTimeOffset localNow) => policy switch
    {
        NumberResetPolicy.Yearly => localNow.ToString("yyyy", CultureInfo.InvariantCulture),
        NumberResetPolicy.Monthly => localNow.ToString("yyyyMM", CultureInfo.InvariantCulture),
        NumberResetPolicy.Daily => localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>
    /// The last number issued in the current period: the counter's value when it belongs to this period, otherwise 0 (the
    /// next number starts again from 1, as the allocation does).
    /// </summary>
    public static long LastIssued(long currentValue, string? currentPeriod, NumberResetPolicy policy, DateTimeOffset localNow) =>
        string.Equals(currentPeriod, PeriodFor(policy, localNow), StringComparison.Ordinal) ? currentValue : 0;

    /// <summary>
    /// The counter after an administrator's change. A new next number counts from there in the current period. A new
    /// reset keeps counting in the current period and starts again from 1 only at its next boundary, so no number already
    /// issued this period is issued again. Otherwise the counter is left as it is.
    /// </summary>
    public static NumberCounter CounterAfterChange(
        NumberCounter current, NumberResetPolicy currentReset, NumberResetPolicy newReset, long? startAt, DateTimeOffset localNow)
    {
        if (startAt is { } next)
        {
            return new NumberCounter(next - 1, PeriodFor(newReset, localNow));
        }

        return newReset == currentReset
            ? current
            : new NumberCounter(LastIssued(current.Value, current.Period, currentReset, localNow), PeriodFor(newReset, localNow));
    }

    private static NumberFormatPart Classify(string raw)
    {
        var name = raw[1..^1];
        var kind = name switch
        {
            "FAC" => NumberFormatPartKind.FacilityCode,
            "YYYY" => NumberFormatPartKind.Year,
            "YY" => NumberFormatPartKind.ShortYear,
            "MM" => NumberFormatPartKind.Month,
            "DD" => NumberFormatPartKind.Day,
            "SEQ" => NumberFormatPartKind.Sequence,
            _ => NumberFormatPartKind.Unknown,
        };
        if (kind != NumberFormatPartKind.Unknown)
        {
            return new NumberFormatPart(kind, raw);
        }

        // {SEQ:n}: n is checked by the rules (1 to 12); anything but digits after the colon is not a part.
        var digits = name.StartsWith("SEQ:", StringComparison.Ordinal) ? name[4..] : string.Empty;
        return digits.Length is > 0 and <= 9 && digits.All(char.IsAsciiDigit)
            ? new NumberFormatPart(NumberFormatPartKind.Sequence, raw, int.Parse(digits, CultureInfo.InvariantCulture))
            : new NumberFormatPart(NumberFormatPartKind.Unknown, raw);
    }
}
