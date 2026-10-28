namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>
/// Hospital-calendar helpers (IST). Bed days, nursing shifts, fluid-balance days and census dates are local calendar
/// concepts while every instant is stored in UTC; these helpers are the single place where the two meet, so a bed day
/// or a shift boundary is computed identically by the API, the background jobs and the unit tests.
/// </summary>
public static class IndiaClock
{
    public static TimeZoneInfo Zone { get; } = ResolveZone();

    public static DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);

    public static TimeOnly LocalTime(DateTimeOffset instant) => TimeOnly.FromDateTime(ToLocal(instant).DateTime);

    /// <summary>UTC instant of local midnight at the start of <paramref name="date"/>.</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date) => AtLocal(date, TimeOnly.MinValue);

    /// <summary>UTC instant of a local wall-clock time on a local date.</summary>
    public static DateTimeOffset AtLocal(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static TimeZoneInfo ResolveZone() => HospitalTimeZone.Zone;
}
