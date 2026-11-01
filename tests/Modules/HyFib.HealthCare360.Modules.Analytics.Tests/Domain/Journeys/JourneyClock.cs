namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys;

/// <summary>A morning in the hospital: 31 Oct 2026, with times on the IST clock the wards read.</summary>
internal static class JourneyClock
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    /// <summary>11:00 IST, when the pipeline is looked at.</summary>
    public static readonly DateTimeOffset Now = At(11, 0);

    public static DateTimeOffset At(int hour, int minute) => new DateTimeOffset(2026, 10, 31, hour, minute, 0, Ist).ToUniversalTime();

    public static DateTimeOffset DaysAgo(int days, int hour, int minute) => At(hour, minute).AddDays(-days);
}
