using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Hospital-local (IST) calendar helpers. Query parameters carry local calendar dates, instants are stored in UTC, and
/// "today" (for the QC lockout, worklists and age at collection) is always the hospital's day, not the server's.
/// </summary>
internal static class DiagnosticsTime
{
    /// <summary>Clock skew tolerated when a workstation supplies a time (collection, QC run, call time).</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    private static readonly Lazy<TimeZoneInfo> Zone = new(() =>
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

    public static DateOnly Today(TimeProvider time) => time.Today();

    public static DateTimeOffset StartOfDayUtc(DateOnly localDate)
    {
        var start = localDate.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(start, Zone.Value.GetUtcOffset(start)).ToUniversalTime();
    }

    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone.Value).DateTime);

    public static DateTimeOffset LocalTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone.Value);

    /// <summary>
    /// UTC bounds [start, end) for local dates from..to (inclusive). Missing dates default to today, a reversed range
    /// is swapped, and the span is capped at <paramref name="maxDays"/> to protect the server on WiFi.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Range(DateOnly? from, DateOnly? to, TimeProvider time, int maxDays = 93)
    {
        var today = Today(time);
        var first = from ?? to ?? today;
        var last = to ?? from ?? today;
        if (last < first)
        {
            (first, last) = (last, first);
        }

        if (last.DayNumber - first.DayNumber >= maxDays)
        {
            first = last.AddDays(-(maxDays - 1));
        }

        return (StartOfDayUtc(first), StartOfDayUtc(last.AddDays(1)));
    }
}

/// <summary>Bounds text arriving from other modules' events to the column sizes, so a long order note never fails an order.</summary>
internal static class TextLimits
{
    public static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    public static string? ClipOrNull(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : Clip(value.Trim(), max);
}
