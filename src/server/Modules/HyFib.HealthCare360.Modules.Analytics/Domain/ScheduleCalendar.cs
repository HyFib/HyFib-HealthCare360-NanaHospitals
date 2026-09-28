namespace HyFib.HealthCare360.Modules.Analytics.Domain;

public enum ScheduleFrequency
{
    Daily = 0,
    Weekly,
    Monthly,
}

/// <summary>
/// When a scheduled report e-mail is due (pure and unit-tested). Times are IST wall-clock times; weekly schedules run on
/// one weekday; monthly schedules run on a day of the month (1–28) or on its last day (<see cref="LastDayOfMonth"/>), so
/// a month-end MIS pack arrives on 28/29 February and 30 April as well.
/// </summary>
public static class ScheduleCalendar
{
    /// <summary>Day-of-month value meaning "the last day of the month".</summary>
    public const int LastDayOfMonth = 31;

    /// <summary>The first run strictly after <paramref name="after"/> (UTC).</summary>
    public static DateTimeOffset NextRun(ScheduleFrequency frequency, TimeOnly timeOfDay, Weekday? dayOfWeek, int? dayOfMonth, DateTimeOffset after)
    {
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
        ScheduleFrequency.Daily => true,
        ScheduleFrequency.Weekly => ToWeekday(date.DayOfWeek) == (dayOfWeek ?? Weekday.Monday),
        _ => date.Day == Math.Min(Math.Clamp(dayOfMonth ?? 1, 1, 31), DateTime.DaysInMonth(date.Year, date.Month)),
    };

    public static Weekday ToWeekday(DayOfWeek day) => day == DayOfWeek.Sunday ? Weekday.Sunday : (Weekday)(int)day;

    /// <summary>"Every day at 07:00", "Every Monday at 08:30", "On day 1 of every month at 09:00".</summary>
    public static string Describe(ScheduleFrequency frequency, TimeOnly timeOfDay, Weekday? dayOfWeek, int? dayOfMonth)
    {
        var time = timeOfDay.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return frequency switch
        {
            ScheduleFrequency.Daily => $"Every day at {time} IST",
            ScheduleFrequency.Weekly => $"Every {dayOfWeek ?? Weekday.Monday} at {time} IST",
            _ => dayOfMonth == LastDayOfMonth ? $"On the last day of every month at {time} IST" : $"On day {dayOfMonth ?? 1} of every month at {time} IST",
        };
    }
}
