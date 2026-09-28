namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Hospital-local (IST) time helpers. Attendance, rosters and payroll months are local calendar concepts, while
/// punches are stored as UTC instants; all conversions go through here so night shifts and month ends agree.
/// </summary>
public static class HrClock
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

    public static TimeZoneInfo Zone => IndiaZone.Value;

    /// <summary>Today's date in the hospital time zone.</summary>
    public static DateOnly LocalToday(this TimeProvider time) => DateOnly.FromDateTime(ToLocal(time.GetUtcNow()));

    public static DateTime LocalNow(this TimeProvider time) => ToLocal(time.GetUtcNow());

    /// <summary>UTC instant → local wall-clock time.</summary>
    public static DateTime ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone).DateTime;

    /// <summary>Local wall-clock time (for example a terminal timestamp) → UTC instant.</summary>
    public static DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, Zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    /// <summary>UTC instant at which the local <paramref name="date"/> starts.</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date) => ToUtc(date.ToDateTime(TimeOnly.MinValue));
}
