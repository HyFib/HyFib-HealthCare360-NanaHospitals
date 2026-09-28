namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

/// <summary>Days with at least one reading in the window (RPM-04).</summary>
public sealed record AdherenceSummary(int WindowDays, int DaysWithReadings, decimal Percent, IReadOnlyList<DateOnly> MissedDates);

/// <summary>
/// Adherence = share of days (IST calendar) with at least one reading, over the last N days but never before the
/// patient was enrolled (a patient enrolled five days ago is not 17 % adherent over 30 days) and never after the
/// enrolment ended.
/// </summary>
public static class Adherence
{
    public const int DefaultWindowDays = 30;

    public static AdherenceSummary Compute(IEnumerable<DateOnly> readingDates, DateOnly today, DateOnly enrolledOn, int windowDays = DefaultWindowDays, DateOnly? endedOn = null)
    {
        var window = Math.Clamp(windowDays, 1, 365);
        var last = endedOn is { } ended && ended < today ? ended : today;
        var first = last.AddDays(-(window - 1));
        if (enrolledOn > first)
        {
            first = enrolledOn;
        }

        if (first > last)
        {
            return new AdherenceSummary(0, 0, 0m, []);
        }

        var days = last.DayNumber - first.DayNumber + 1;
        var withReadings = readingDates.Where(d => d >= first && d <= last).ToHashSet();
        var missed = Enumerable.Range(0, days).Select(first.AddDays).Where(d => !withReadings.Contains(d)).ToList();
        return new AdherenceSummary(days, withReadings.Count, Math.Round(withReadings.Count * 100m / days, 1, MidpointRounding.AwayFromZero), missed);
    }
}
