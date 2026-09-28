using System.Globalization;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// A reporting window of hospital-local (IST) dates, inclusive. It defaults to the last twelve calendar months up to
/// today. It is capped at three years, so an analytics request can never scan the whole history by accident.
/// </summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To)
{
    public const int MaxDays = 3 * 366;

    public DateTimeOffset SinceUtc => EngagementClock.StartOfDayUtc(From);

    /// <summary>Exclusive upper bound: the start of the day after <see cref="To"/>.</summary>
    public DateTimeOffset UntilUtc => EngagementClock.StartOfDayUtc(To.AddDays(1));

    /// <summary>"yyyy-MM" keys of every calendar month the period touches, oldest first.</summary>
    public IReadOnlyList<string> MonthKeys()
    {
        var keys = new List<string>();
        for (var month = new DateOnly(From.Year, From.Month, 1); month <= To; month = month.AddMonths(1))
        {
            keys.Add(month.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        }

        return keys;
    }

    public static Result<ReportPeriod> Resolve(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var end = to ?? today;
        var start = from ?? new DateOnly(end.Year, end.Month, 1).AddMonths(-11);
        if (end < start)
        {
            return Error.Field("to", "The period ends before it starts.");
        }

        return end.DayNumber - start.DayNumber > MaxDays
            ? Error.Field("from", "Choose a period of at most three years.")
            : new ReportPeriod(start, end);
    }
}
