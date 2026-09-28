namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

/// <summary>
/// Converts between the hospital's wall-clock calendar (India Standard Time by default) and UTC instants.
/// Doctor schedules are defined in local time ("Mondays 09:00–13:00") while appointments are stored as UTC
/// instants, so every conversion goes through this one type. It holds no clock, which keeps the slot engine
/// deterministic and unit-testable; "today" and "now" always come from <see cref="TimeProvider"/> via the
/// Patients helpers (<c>time.Today()</c>, <c>PatientQueries.IndiaTime</c>).
/// </summary>
public sealed class HospitalCalendar(TimeZoneInfo zone)
{
    /// <summary>India Standard Time (UTC+05:30, no daylight saving).</summary>
    public static HospitalCalendar India { get; } = new(ResolveIndiaZone());

    public TimeZoneInfo Zone { get; } = zone;

    /// <summary>The UTC instant of a local calendar date and wall-clock time.</summary>
    public DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local))
        {
            // Skipped by a daylight-saving jump (never happens in IST; kept for other zones).
            local = local.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone), TimeSpan.Zero);
    }

    public DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    public DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);

    public TimeOnly TimeOf(DateTimeOffset instant) => TimeOnly.FromDateTime(ToLocal(instant).DateTime);

    /// <summary>UTC instant of local midnight at the start of <paramref name="date"/>.</summary>
    public DateTimeOffset StartOfDay(DateOnly date) => ToUtc(date, TimeOnly.MinValue);

    public static Weekday WeekdayOf(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Sunday ? Weekday.Sunday : (Weekday)(int)date.DayOfWeek;

    private static TimeZoneInfo ResolveIndiaZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Asia/Kolkata", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");
        }
    }
}
