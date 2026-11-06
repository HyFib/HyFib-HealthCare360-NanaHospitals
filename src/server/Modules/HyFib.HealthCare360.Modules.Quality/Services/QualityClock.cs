namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>Hospital-local (IST) dates for due dates, review dates and dashboard months.</summary>
public static class QualityClock
{
    private static readonly Lazy<TimeZoneInfo> IndiaZone = new(() =>
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");
        }
    });

    public static DateOnly LocalToday(this TimeProvider time) => LocalDate(time.GetUtcNow());

    /// <summary>The local (IST) calendar day of an instant: an incident at 1 am IST happened that day, not the day before.</summary>
    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, IndiaZone.Value).DateTime);

    /// <summary>UTC instant at which the local <paramref name="date"/> starts.</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, IndiaZone.Value.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Local calendar month of an instant ("2026-09"), for monthly charts.</summary>
    public static string LocalMonth(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, IndiaZone.Value).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
