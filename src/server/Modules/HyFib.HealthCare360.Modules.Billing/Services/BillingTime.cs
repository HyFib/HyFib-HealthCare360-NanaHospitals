namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Hospital-local (IST) calendar helpers. Instants are stored in UTC, but a "day" at the cash counter, in the day-end
/// report, in tariff validity and in the Tally day book is an Indian calendar day.
/// </summary>
public static class BillingTime
{
    private static readonly Lazy<TimeZoneInfo> Zone = new(() => HospitalTimeZone.Zone);

    public static DateTimeOffset LocalNow(TimeProvider time) => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone.Value);

    public static DateOnly Today(TimeProvider time) => LocalDate(time.GetUtcNow());

    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone.Value).DateTime);

    /// <summary>The instant in hospital time, for messages that name a time.</summary>
    public static DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone.Value);

    /// <summary>UTC instant at which the local calendar day begins.</summary>
    public static DateTimeOffset StartOfDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, Zone.Value.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Half-open UTC range [start, end) covering the local dates <paramref name="from"/>..<paramref name="to"/>.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Range(DateOnly from, DateOnly to) => (StartOfDay(from), StartOfDay(to.AddDays(1)));
}
