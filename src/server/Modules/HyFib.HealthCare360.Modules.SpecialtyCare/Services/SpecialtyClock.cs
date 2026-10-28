namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Hospital-local (IST) calendar helpers. Dates in query strings are local calendar dates; instants are stored in
/// UTC, so every "day" filter converts the local day into a UTC range here. "Today" comes from
/// <c>PatientQueries.Today(TimeProvider)</c>.
/// </summary>
public static class SpecialtyClock
{
    private static readonly Lazy<TimeZoneInfo> Zone = new(() => HospitalTimeZone.Zone);

    public static DateTimeOffset StartOfDayUtc(DateOnly localDate)
    {
        var start = localDate.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(start, Zone.Value.GetUtcOffset(start)).ToUniversalTime();
    }

    /// <summary>[start of <paramref name="from"/>, start of the day after <paramref name="toInclusive"/>) in UTC.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) RangeUtc(DateOnly from, DateOnly toInclusive) =>
        (StartOfDayUtc(from), StartOfDayUtc(toInclusive.AddDays(1)));

    public static DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone.Value);

    public static DateOnly ToLocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant).DateTime);
}
