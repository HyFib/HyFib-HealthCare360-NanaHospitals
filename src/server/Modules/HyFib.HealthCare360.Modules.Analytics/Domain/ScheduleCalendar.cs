namespace HyFib.HealthCare360.Modules.Analytics.Domain;

public enum ScheduleFrequency
{
    Daily = 0,
    Weekly,
    Monthly,

    /// <summary>Every hour, at the minute of the schedule's time. BI extracts only: an e-mail every hour would be noise.</summary>
    Hourly,
}

/// <summary>
/// When a scheduled report is due (pure and unit-tested). Times are IST wall-clock times; hourly schedules run at a
/// minute past every hour; weekly schedules run on one weekday; monthly schedules run on a day of the month (1–28) or on
/// its last day (<see cref="LastDayOfMonth"/>), so a month-end MIS pack arrives on 28/29 February and 30 April as well.
/// </summary>
public static class ScheduleCalendar
{
    /// <summary>Day-of-month value meaning "the last day of the month".</summary>
    public const int LastDayOfMonth = 31;

    /// <summary>The first run strictly after <paramref name="after"/> (UTC).</summary>
    public static DateTimeOffset NextRun(ScheduleFrequency frequency, TimeOnly timeOfDay, Weekday? dayOfWeek, int? dayOfMonth, DateTimeOffset after)
    {
        if (frequency == ScheduleFrequency.Hourly)
        {
            var local = AnalyticsClock.ToLocal(after);
            var run = new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, timeOfDay.Minute, 0, AnalyticsClock.Offset);
            return (run > after ? run : run.AddHours(1)).ToUniversalTime();
        }

        var date = AnalyticsClock.LocalDate(after);

        // Monthly schedules may need up to a month; allow a little more for safety.
        for (var i = 0; i <= 62; i++)
        {
            var candidateDate = date.AddDays(i);
            if (!Matches(frequency, candidateDate, dayOfWeek, dayOfMonth))
            {
                continue;
            }

            var candidate = AnalyticsClock.At(candidateDate, timeOfDay);
            if (candidate > after)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The schedule never matches a calendar day.");
    }

    public static bool Matches(ScheduleFrequency frequency, DateOnly date, Weekday? dayOfWeek, int? dayOfMonth) => frequency switch
    {
        ScheduleFrequency.Daily or ScheduleFrequency.Hourly => true,
        ScheduleFrequency.Weekly => ToWeekday(date.DayOfWeek) == (dayOfWeek ?? Weekday.Monday),
        _ => date.Day == Math.Min(Math.Clamp(dayOfMonth ?? 1, 1, 31), DateTime.DaysInMonth(date.Year, date.Month)),
    };

    public static Weekday ToWeekday(DayOfWeek day) => day == DayOfWeek.Sunday ? Weekday.Sunday : (Weekday)(int)day;

    /// <summary>
    /// The longest time between two runs: an extract not refreshed for this long and <see cref="LateMargin"/> is late.
    /// A month is taken as 31 days.
    /// </summary>
    public static TimeSpan Interval(ScheduleFrequency frequency) => frequency switch
    {
        ScheduleFrequency.Hourly => TimeSpan.FromHours(1),
        ScheduleFrequency.Daily => TimeSpan.FromDays(1),
        ScheduleFrequency.Weekly => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(31),
    };

    /// <summary>How much longer than its interval a refresh may take before the extract counts as late.</summary>
    public static readonly TimeSpan LateMargin = TimeSpan.FromMinutes(30);

    /// <summary>"Every hour at 15 minutes past", "Every day at 07:00", "Every Monday at 08:30", "On day 1 of every month at 09:00".</summary>
    public static string Describe(ScheduleFrequency frequency, TimeOnly timeOfDay, Weekday? dayOfWeek, int? dayOfMonth)
    {
        var time = timeOfDay.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return frequency switch
        {
            ScheduleFrequency.Hourly => timeOfDay.Minute == 0 ? "Every hour on the hour" : $"Every hour at {timeOfDay.Minute} minutes past",
            ScheduleFrequency.Daily => $"Every day at {time} IST",
            ScheduleFrequency.Weekly => $"Every {dayOfWeek ?? Weekday.Monday} at {time} IST",
            _ => dayOfMonth == LastDayOfMonth ? $"On the last day of every month at {time} IST" : $"On day {dayOfMonth ?? 1} of every month at {time} IST",
        };
    }
}
