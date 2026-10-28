namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Hospital-calendar helpers. Dates arriving in query strings are local (IST) calendar dates, while instants are
/// stored in UTC; these helpers convert between the two consistently with the Patients module.
/// </summary>
public static class ClinicalClock
{
    private static readonly Lazy<TimeZoneInfo> Zone = new(() => HospitalTimeZone.Zone);

    /// <summary>UTC instant at which the local calendar day starts.</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly localDate)
    {
        var start = localDate.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(start, Zone.Value.GetUtcOffset(start)).ToUniversalTime();
    }

    /// <summary>Half-open UTC range [from 00:00, to + 1 day 00:00) for inclusive local dates.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) RangeUtc(DateOnly from, DateOnly to) =>
        (StartOfDayUtc(from), StartOfDayUtc(to.AddDays(1)));

    /// <summary>Local calendar date of an instant.</summary>
    public static DateOnly LocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone.Value).DateTime);
}
