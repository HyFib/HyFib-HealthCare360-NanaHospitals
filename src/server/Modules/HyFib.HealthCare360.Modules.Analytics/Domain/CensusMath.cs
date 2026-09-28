namespace HyFib.HealthCare360.Modules.Analytics.Domain;

/// <summary>
/// Midnight-census arithmetic (WB-06), identical to the Inpatient module's census: a patient counts on day <c>d</c> when
/// they were admitted before the end of <c>d</c> (the next IST midnight) and not discharged before it. Working on IST
/// day numbers (<see cref="DateOnly.DayNumber"/>, which PostgreSQL computes as <c>date - DATE '0001-01-01'</c>) lets the
/// report queries return two small integers per stay instead of loading admissions.
/// </summary>
public static class CensusMath
{
    /// <summary>
    /// Inpatient days a stay contributes to the IST period <paramref name="fromDay"/>..<paramref name="toDay"/> (inclusive).
    /// <paramref name="startDay"/> is the IST date the stay began; <paramref name="endDay"/> the IST date it ended, or
    /// null while the patient is still in. The patient is counted at the midnight ending each day from the admission day up
    /// to the day before discharge, so a same-day admission and discharge contributes no census day.
    /// </summary>
    public static int CensusDays(int startDay, int? endDay, int fromDay, int toDay)
    {
        var first = Math.Max(fromDay, startDay);
        var last = endDay is { } end ? Math.Min(toDay, end - 1) : toDay;
        return Math.Max(0, last - first + 1);
    }

    /// <summary>The same count from instants (converted to IST dates).</summary>
    public static int CensusDays(DateTimeOffset start, DateTimeOffset? end, DateOnly from, DateOnly to) =>
        CensusDays(AnalyticsClock.LocalDate(start).DayNumber, end is { } e ? AnalyticsClock.LocalDate(e).DayNumber : null, from.DayNumber, to.DayNumber);

    /// <summary>
    /// Patients in a bed at the midnight that ends <paramref name="day"/> (0 or 1 for one stay): the daily census used by
    /// trend charts.
    /// </summary>
    public static bool InCensus(int startDay, int? endDay, int day) => startDay <= day && (endDay is null || endDay > day);
}
