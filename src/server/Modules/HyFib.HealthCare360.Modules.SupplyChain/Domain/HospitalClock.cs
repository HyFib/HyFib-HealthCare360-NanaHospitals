namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>
/// Hospital calendar helpers (IST by default). Expiry, reorder and maintenance due dates are calendar dates in
/// the hospital's time zone while instants are stored in UTC. SupplyChain sits beside Patients in layer 2 and
/// cannot use its helpers, so it carries its own; higher modules may use either.
/// </summary>
public static class HospitalClock
{
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

    public static DateTimeOffset LocalNow(TimeProvider time) => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone.Value);

    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(LocalNow(time).DateTime);

    public static DateOnly ToLocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone.Value).DateTime);

    /// <summary>The instant in hospital time (for printed registers and slips).</summary>
    public static DateTimeOffset ToLocalTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone.Value);

    /// <summary>UTC instant at which a local calendar day starts (used for date-range filters on instants).</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly localDate)
    {
        var start = localDate.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(start, Zone.Value.GetUtcOffset(start)).ToUniversalTime();
    }

    /// <summary>Half-open UTC range [from 00:00, day after <paramref name="to"/> 00:00) for local calendar dates.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Range(DateOnly from, DateOnly to) =>
        (StartOfDayUtc(from), StartOfDayUtc(to.AddDays(1)));
}
