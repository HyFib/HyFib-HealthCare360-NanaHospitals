using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Laboratory;

/// <summary>
/// Analyser timestamps: HL7 DTM and ASTM date/times (<c>YYYY[MM[DD[HH[MM[SS[.S…]]]]]][±HHMM]</c>). Analysers report
/// local wall-clock time, so a value without an offset is interpreted in the facility's zone (IST by default).
/// </summary>
internal static class LabTime
{
    private static readonly TimeZoneInfo IndiaStandardTime =
        TimeZoneInfo.CreateCustomTimeZone("HC360-IST", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");

    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Fall through: a misconfigured zone must not stop results from flowing.
            }
        }

        return IndiaStandardTime;
    }

    public static DateTimeOffset? Parse(string? value, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        TimeSpan? offset = null;
        var sign = text.Length > 4 ? text.IndexOfAny(['+', '-'], 4) : -1;
        if (sign > 0)
        {
            offset = ParseOffset(text[sign..]);
            if (offset is null)
            {
                return null;
            }

            text = text[..sign];
        }

        var fractionTicks = 0L;
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            if (double.TryParse("0" + text[dot..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fraction))
            {
                fractionTicks = (long)(fraction * TimeSpan.TicksPerSecond);
            }

            text = text[..dot];
        }

        if (text.Length is not (4 or 6 or 8 or 10 or 12 or 14) || !text.All(char.IsAsciiDigit))
        {
            return null;
        }

        try
        {
            var local = new DateTime(
                Part(text, 0, 4, 1), Part(text, 4, 2, 1), Part(text, 6, 2, 1),
                Part(text, 8, 2, 0), Part(text, 10, 2, 0), Part(text, 12, 2, 0), DateTimeKind.Unspecified).AddTicks(fractionTicks);
            return new DateTimeOffset(local, offset ?? zone.GetUtcOffset(local));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int Part(string text, int start, int length, int fallback) =>
        text.Length >= start + length ? int.Parse(text.AsSpan(start, length), NumberStyles.None, CultureInfo.InvariantCulture) : fallback;

    private static TimeSpan? ParseOffset(string value)
    {
        if (value.Length != 5
            || !int.TryParse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(value.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || hours > 14 || minutes > 59)
        {
            return null;
        }

        var span = new TimeSpan(hours, minutes, 0);
        return value[0] == '-' ? -span : span;
    }
}
