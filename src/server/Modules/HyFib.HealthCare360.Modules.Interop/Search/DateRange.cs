using System.Globalization;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Interop.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Search;

/// <summary>
/// The implicit range [Start, End) of a FHIR date, dateTime or instant at its precision: "2026" covers the whole year,
/// "2026-09-01" the day, "2026-09-01T10:15" the minute, "…:15.123" the millisecond. Values without a time zone are IST
/// (the server's local time, as FHIR recommends for search values).
/// </summary>
internal readonly record struct DateRange(DateTimeOffset Start, DateTimeOffset End)
{
    /// <summary>An open-ended range (an ongoing period).</summary>
    public static DateRange From(DateTimeOffset start) => new(start, DateTimeOffset.MaxValue);

    /// <summary>A stored instant at millisecond precision.</summary>
    public static DateRange Instant(DateTimeOffset value) => new(value, value.AddTicks(TimeSpan.TicksPerMillisecond));

    /// <summary>True when the value is a date without a time (year, year-month or full date).</summary>
    public static bool IsDateOnly(string value) => !value.Contains('T', StringComparison.Ordinal);

    public static bool TryParse(string? value, out DateRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(value) || DatePatterns.Value().Match(value.Trim()) is not { Success: true } match)
        {
            return false;
        }

        try
        {
            var year = Number(match, "y");
            if (!match.Groups["M"].Success)
            {
                var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, FhirTime.Ist);
                range = new DateRange(start, start.AddYears(1));
                return true;
            }

            var month = Number(match, "M");
            if (!match.Groups["d"].Success)
            {
                var start = new DateTimeOffset(year, month, 1, 0, 0, 0, FhirTime.Ist);
                range = new DateRange(start, start.AddMonths(1));
                return true;
            }

            var day = Number(match, "d");
            if (!match.Groups["h"].Success)
            {
                var start = new DateTimeOffset(year, month, day, 0, 0, 0, FhirTime.Ist);
                range = new DateRange(start, start.AddDays(1));
                return true;
            }

            var offset = Offset(match.Groups["tz"]);
            var hasSeconds = match.Groups["s"].Success;
            var time = new DateTimeOffset(year, month, day, Number(match, "h"), Number(match, "m"), hasSeconds ? Number(match, "s") : 0, offset);
            if (!hasSeconds)
            {
                range = new DateRange(time, time.AddMinutes(1));
                return true;
            }

            var fraction = match.Groups["f"].Success ? match.Groups["f"].Value : string.Empty;
            if (fraction.Length == 0)
            {
                range = new DateRange(time, time.AddSeconds(1));
                return true;
            }

            var digits = Math.Min(fraction.Length, 7);
            var ticks = long.Parse(fraction[..digits], CultureInfo.InvariantCulture) * (long)Math.Pow(10, 7 - digits);
            var precise = time.AddTicks(ticks);
            range = new DateRange(precise, precise.AddTicks((long)Math.Pow(10, 7 - digits)));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int Number(Match match, string group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static TimeSpan Offset(Group tz)
    {
        if (!tz.Success)
        {
            return FhirTime.Ist;
        }

        if (tz.Value == "Z")
        {
            return TimeSpan.Zero;
        }

        var hours = int.Parse(tz.Value.AsSpan(1, 2), CultureInfo.InvariantCulture);
        var minutes = int.Parse(tz.Value.AsSpan(4, 2), CultureInfo.InvariantCulture);
        var offset = new TimeSpan(hours, minutes, 0);
        return tz.Value[0] == '-' ? -offset : offset;
    }
}

/// <summary>The FHIR date / dateTime / instant lexical form (with optional time, seconds, fraction and zone).</summary>
internal static partial class DatePatterns
{
    [GeneratedRegex(@"^(?<y>\d{4})(-(?<M>\d{2})(-(?<d>\d{2})(T(?<h>\d{2}):(?<m>\d{2})(:(?<s>\d{2})(\.(?<f>\d{1,9}))?)?(?<tz>Z|[+-]\d{2}:\d{2})?)?)?)?$", RegexOptions.CultureInvariant)]
    public static partial Regex Value();
}

/// <summary>Search prefixes for dates (FHIR R4 §3.1.1.4.2); <c>sa</c>, <c>eb</c> and <c>ap</c> are not supported.</summary>
internal enum SearchPrefix
{
    Eq = 0,
    Ne,
    Gt,
    Lt,
    Ge,
    Le,
}

/// <summary>A date search value such as <c>ge2026-01-01</c> and its FHIR matching semantics.</summary>
internal sealed record DateParameter(SearchPrefix Prefix, DateRange Range)
{
    private static readonly Dictionary<string, SearchPrefix> Prefixes = new(StringComparer.Ordinal)
    {
        ["eq"] = SearchPrefix.Eq,
        ["ne"] = SearchPrefix.Ne,
        ["gt"] = SearchPrefix.Gt,
        ["lt"] = SearchPrefix.Lt,
        ["ge"] = SearchPrefix.Ge,
        ["le"] = SearchPrefix.Le,
    };

    private static readonly string[] UnsupportedPrefixes = ["sa", "eb", "ap"];

    public static bool TryParse(string raw, out DateParameter? parameter, out string? error)
    {
        parameter = null;
        error = null;
        var value = raw.Trim();
        var prefix = SearchPrefix.Eq;
        if (value.Length > 2 && char.IsAsciiLetterLower(value[0]) && char.IsAsciiLetterLower(value[1]))
        {
            var code = value[..2];
            if (UnsupportedPrefixes.Contains(code, StringComparer.Ordinal))
            {
                error = $"The date prefix '{code}' is not supported; use eq, ne, gt, ge, lt or le.";
                return false;
            }

            if (!Prefixes.TryGetValue(code, out prefix))
            {
                error = $"'{FhirValue(value)}' is not a valid date search value.";
                return false;
            }

            value = value[2..];
        }

        if (!DateRange.TryParse(value, out var range))
        {
            error = $"'{FhirValue(raw)}' is not a valid FHIR date (yyyy, yyyy-MM, yyyy-MM-dd or yyyy-MM-ddThh:mm[:ss][Z|±hh:mm]).";
            return false;
        }

        parameter = new DateParameter(prefix, range);
        return true;
    }

    /// <summary>
    /// FHIR range semantics: eq when the search range contains the target range; gt/lt when the target extends beyond
    /// the search range above/below it; ge/le add eq to those.
    /// </summary>
    public bool Matches(DateRange target)
    {
        var contains = Range.Start <= target.Start && target.End <= Range.End;
        return Prefix switch
        {
            SearchPrefix.Eq => contains,
            SearchPrefix.Ne => !contains,
            SearchPrefix.Gt => target.End > Range.End,
            SearchPrefix.Lt => target.Start < Range.Start,
            SearchPrefix.Ge => target.End > Range.End || contains,
            SearchPrefix.Le => target.Start < Range.Start || contains,
            _ => false,
        };
    }

    /// <summary>
    /// The same rule for a single stored instant, as SQL-friendly bounds: <c>From</c> ≤ value &lt; <c>Before</c>, or
    /// outside that interval when <c>Negate</c> (ne).
    /// </summary>
    public (DateTimeOffset? From, DateTimeOffset? Before, bool Negate) InstantBounds() => Prefix switch
    {
        SearchPrefix.Eq => (Range.Start, Range.End, false),
        SearchPrefix.Ne => (Range.Start, Range.End, true),
        SearchPrefix.Gt => (Range.End, null, false),
        SearchPrefix.Ge => (Range.Start, null, false),
        SearchPrefix.Lt => (null, Range.Start, false),
        SearchPrefix.Le => (null, Range.End, false),
        _ => (null, null, false),
    };

    private static string FhirValue(string value) => value.Length > 40 ? value[..40] + "…" : value;
}
