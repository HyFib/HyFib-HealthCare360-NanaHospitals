namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>A day to be computed and the (possibly combined) shift rostered on it.</summary>
public sealed record AttendanceDayPlan(DateOnly Date, ShiftTimes? Shift);

/// <summary>
/// Decides which day each punch belongs to. A night shift's out-punch happens on the next calendar date, so
/// punches cannot simply be grouped by date. A day with a shift owns the window from 4 hours before its start
/// to 6 hours after its end; a day without a shift owns its calendar day. Where two windows overlap (night shift
/// followed by a morning shift) the boundary is the midpoint between the first shift's end and the second's start.
/// Each punch is allocated to at most one day.
/// </summary>
public static class PunchAllocator
{
    public static readonly TimeSpan EarlyArrival = TimeSpan.FromHours(4);
    public static readonly TimeSpan LateDeparture = TimeSpan.FromHours(6);

    public static IReadOnlyDictionary<DateOnly, IReadOnlyList<DateTime>> Allocate(IReadOnlyList<AttendanceDayPlan> days, IEnumerable<DateTime> punches)
    {
        var windows = BuildWindows(days);
        var result = windows.ToDictionary(w => w.Date, _ => new List<DateTime>());

        foreach (var punch in punches.Order())
        {
            foreach (var window in windows)
            {
                if (punch >= window.From && punch < window.To)
                {
                    result[window.Date].Add(punch);
                    break;
                }
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<DateTime>)kv.Value);
    }

    /// <summary>The non-overlapping [From, To) window owned by each day, in date order.</summary>
    public static IReadOnlyList<(DateOnly Date, DateTime From, DateTime To)> BuildWindows(IReadOnlyList<AttendanceDayPlan> days)
    {
        var ordered = days.OrderBy(d => d.Date).ToList();
        var windows = ordered.Select(d => d.Shift is { } shift
                ? (d.Date, From: shift.StartOn(d.Date) - EarlyArrival, To: shift.EndOn(d.Date) + LateDeparture)
                : (d.Date, From: d.Date.ToDateTime(TimeOnly.MinValue), To: d.Date.AddDays(1).ToDateTime(TimeOnly.MinValue)))
            .ToList();

        for (var i = 0; i + 1 < windows.Count; i++)
        {
            var current = windows[i];
            var next = windows[i + 1];
            if (current.To <= next.From)
            {
                continue;
            }

            var boundary = Boundary(ordered[i], ordered[i + 1], current.To, next.From);
            windows[i] = (current.Date, current.From, boundary);
            windows[i + 1] = (next.Date, boundary, next.To);
        }

        return windows;
    }

    private static DateTime Boundary(AttendanceDayPlan current, AttendanceDayPlan next, DateTime currentTo, DateTime nextFrom)
    {
        if (current.Shift is { } a && next.Shift is { } b)
        {
            var end = a.EndOn(current.Date);
            var start = b.StartOn(next.Date);
            return end <= start ? end + ((start - end) / 2) : start;
        }

        // A shift beats a shift-less day: a night shift keeps its out-punch even when the next day is a week off.
        return current.Shift is not null ? currentTo : nextFrom;
    }
}
