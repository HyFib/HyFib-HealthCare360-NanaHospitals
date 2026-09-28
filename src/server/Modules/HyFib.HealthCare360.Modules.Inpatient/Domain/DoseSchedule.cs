using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>
/// Turns prescription frequencies used in Indian hospitals (OD, BD, TDS, QID, HS, 1-0-1, Q6H, STAT, SOS…) into
/// administration times on the ward's standard medication rounds, so every nurse sees the same due times. Unknown or
/// "as needed" frequencies produce no schedule: the dose is charted only when given (never guessed).
/// </summary>
public static partial class DoseSchedule
{
    private static readonly Dictionary<string, TimeOnly[]> Rounds = new(StringComparer.Ordinal)
    {
        ["OD"] = [T(9)],
        ["QD"] = [T(9)],
        ["DAILY"] = [T(9)],
        ["ONCEDAILY"] = [T(9)],
        ["QAM"] = [T(8)],
        ["BD"] = [T(9), T(21)],
        ["BID"] = [T(9), T(21)],
        ["TDS"] = [T(8), T(14), T(20)],
        ["TID"] = [T(8), T(14), T(20)],
        ["QID"] = [T(6), T(12), T(18), T(22)],
        ["QDS"] = [T(6), T(12), T(18), T(22)],
        ["HS"] = [T(22)],
        ["NOCTE"] = [T(22)],
        ["QHS"] = [T(22)],
    };

    private static readonly Dictionary<int, int> IntervalAnchors = new() { [1] = 0, [2] = 0, [3] = 0, [4] = 2, [6] = 0, [8] = 6, [12] = 8, [24] = 9 };

    private static readonly string[] AsNeeded = ["SOS", "PRN", "ASNEEDED", "ASREQUIRED", "ASDIRECTED"];

    private static readonly string[] Immediate = ["STAT", "NOW", "IMMEDIATELY", "ONCE"];

    /// <summary>Given only when required (SOS/PRN) or on the doctor's direction: no automatic schedule.</summary>
    public static bool IsAsNeeded(string? frequency) => Normalize(frequency) is { } f && AsNeeded.Contains(f);

    /// <summary>A single dose now (STAT).</summary>
    public static bool IsImmediate(string? frequency) => Normalize(frequency) is { } f && Immediate.Contains(f);

    /// <summary>Local administration times in a day, or empty when the frequency cannot be scheduled.</summary>
    public static IReadOnlyList<TimeOnly> TimesOfDay(string? frequency)
    {
        var f = Normalize(frequency);
        if (f is null || AsNeeded.Contains(f) || Immediate.Contains(f))
        {
            return [];
        }

        if (Rounds.TryGetValue(f, out var times))
        {
            return times;
        }

        var interval = IntervalRegex().Match(f);
        if (interval.Success && int.TryParse(interval.Groups["hours"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours is >= 1 and <= 24)
        {
            var anchor = IntervalAnchors.TryGetValue(hours, out var a) ? a : 6;
            return Enumerable.Range(0, 24 / hours).Select(i => T((anchor + (i * hours)) % 24)).OrderBy(t => t).ToList();
        }

        return PatternTimes(frequency!);
    }

    /// <summary>
    /// Dose instants in [<paramref name="from"/>, <paramref name="to"/>) for the frequency, clipped to the order's validity.
    /// STAT yields a single dose at <paramref name="from"/> (or the order start, whichever is later).
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Between(string? frequency, DateTimeOffset from, DateTimeOffset to, DateTimeOffset? orderStart = null, DateTimeOffset? orderEnd = null)
    {
        var start = orderStart is { } s && s > from ? s : from;
        var end = orderEnd is { } e && e < to ? e : to;
        if (end <= start)
        {
            return [];
        }

        if (IsImmediate(frequency))
        {
            return [start];
        }

        var times = TimesOfDay(frequency);
        if (times.Count == 0)
        {
            return [];
        }

        var doses = new List<DateTimeOffset>();
        for (var date = IndiaClock.LocalDate(start); IndiaClock.StartOfDayUtc(date) < end; date = date.AddDays(1))
        {
            foreach (var time in times)
            {
                var at = IndiaClock.AtLocal(date, time);
                if (at >= start && at < end)
                {
                    doses.Add(at);
                }
            }
        }

        return doses;
    }

    /// <summary>Upper-case, no spaces, dots or hyphens ("b.d." → "BD", "Once daily" → "ONCEDAILY").</summary>
    internal static string? Normalize(string? frequency)
    {
        if (string.IsNullOrWhiteSpace(frequency))
        {
            return null;
        }

        var compact = new string(frequency.Where(c => !char.IsWhiteSpace(c) && c is not '.' and not '-' and not '_').ToArray());
        return compact.Length == 0 ? null : compact.ToUpperInvariant();
    }

    /// <summary>Morning–afternoon–night patterns: "1-0-1", "1-1-1", "0-0-1", "½-0-½", and four-part "1-1-1-1".</summary>
    private static IReadOnlyList<TimeOnly> PatternTimes(string frequency)
    {
        var parts = frequency.Trim().Split(['-', '–'], StringSplitOptions.TrimEntries);
        if (parts.Length is not (3 or 4) || !parts.All(IsDoseToken))
        {
            return [];
        }

        TimeOnly[] slots = parts.Length == 3 ? [T(8), T(14), T(20)] : [T(6), T(12), T(18), T(22)];
        return parts.Select((p, i) => (p, i)).Where(x => !IsZero(x.p)).Select(x => slots[x.i]).ToList();
    }

    private static bool IsDoseToken(string token) => DoseTokenRegex().IsMatch(token);

    private static bool IsZero(string token) => token.All(c => c is '0' or '.');

    private static TimeOnly T(int hour) => new(hour, 0);

    [GeneratedRegex(@"^(Q|EVERY)?(?<hours>\d{1,2})(H|HR|HRS|HOUR|HOURS|HOURLY|HRLY)$")]
    private static partial Regex IntervalRegex();

    [GeneratedRegex(@"^(\d+(\.\d+)?|\d+/\d+|½|¼|¾)$")]
    private static partial Regex DoseTokenRegex();
}
