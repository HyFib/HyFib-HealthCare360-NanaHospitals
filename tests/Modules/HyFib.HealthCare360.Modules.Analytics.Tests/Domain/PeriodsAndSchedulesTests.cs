using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain;

public sealed class PeriodsAndSchedulesTests
{
    private static readonly DateOnly Today = D("2026-09-15"); // a Tuesday

    private static DateOnly D(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(ReportPeriods.Today, "2026-09-15", "2026-09-15")]
    [InlineData(ReportPeriods.Yesterday, "2026-09-14", "2026-09-14")]
    [InlineData(ReportPeriods.Last7Days, "2026-09-09", "2026-09-15")]
    [InlineData(ReportPeriods.Last30Days, "2026-08-17", "2026-09-15")]
    [InlineData(ReportPeriods.ThisWeek, "2026-09-14", "2026-09-15")]
    [InlineData(ReportPeriods.LastWeek, "2026-09-07", "2026-09-13")]
    [InlineData(ReportPeriods.ThisMonth, "2026-09-01", "2026-09-15")]
    [InlineData(ReportPeriods.LastMonth, "2026-08-01", "2026-08-31")]
    [InlineData(ReportPeriods.ThisQuarter, "2026-07-01", "2026-09-15")]
    [InlineData(ReportPeriods.LastQuarter, "2026-04-01", "2026-06-30")]
    [InlineData(ReportPeriods.ThisFinancialYear, "2026-04-01", "2026-09-15")]
    [InlineData(ReportPeriods.LastFinancialYear, "2025-04-01", "2026-03-31")]
    public void Relative_periods_resolve_to_ist_dates(string preset, string from, string to) =>
        ReportPeriods.Resolve(preset, Today).ShouldBe((D(from), D(to)));

    [Fact]
    public void Periods_roll_over_year_boundaries()
    {
        ReportPeriods.Resolve(ReportPeriods.LastMonth, D("2026-01-10")).ShouldBe((D("2025-12-01"), D("2025-12-31")));
        ReportPeriods.Resolve(ReportPeriods.LastQuarter, D("2026-02-10")).ShouldBe((D("2025-10-01"), D("2025-12-31")));
        ReportPeriods.Resolve(ReportPeriods.ThisFinancialYear, D("2026-03-31")).ShouldBe((D("2025-04-01"), D("2026-03-31")));
        ReportPeriods.Resolve(ReportPeriods.ThisFinancialYear, D("2026-04-01")).ShouldBe((D("2026-04-01"), D("2026-04-01")));
    }

    [Fact]
    public void The_previous_period_has_the_same_length_and_ends_the_day_before()
    {
        ReportPeriods.Previous(D("2026-09-01"), D("2026-09-15")).ShouldBe((D("2026-08-17"), D("2026-08-31")));
        ReportPeriods.Previous(D("2026-09-15"), D("2026-09-15")).ShouldBe((D("2026-09-14"), D("2026-09-14")));
    }

    [Fact]
    public void Periods_are_described_in_words()
    {
        ReportPeriods.Describe(D("2026-09-01"), D("2026-09-15")).ShouldBe("01 Sep 2026 – 15 Sep 2026");
        ReportPeriods.Describe(D("2026-09-15"), D("2026-09-15")).ShouldBe("15 Sep 2026");
        ReportPeriods.DefaultFor(ScheduleFrequency.Daily).ShouldBe(ReportPeriods.Yesterday);
        ReportPeriods.DefaultFor(ScheduleFrequency.Weekly).ShouldBe(ReportPeriods.LastWeek);
        ReportPeriods.DefaultFor(ScheduleFrequency.Monthly).ShouldBe(ReportPeriods.LastMonth);
        Should.Throw<ArgumentOutOfRangeException>(() => ReportPeriods.Resolve(ReportPeriods.Custom, Today));
    }

    [Fact]
    public void Daily_schedules_run_today_if_the_time_is_still_ahead_otherwise_tomorrow()
    {
        var at0700 = new TimeOnly(7, 0);
        var morning = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero); // 05:30 IST
        var noon = new DateTimeOffset(2026, 9, 15, 6, 30, 0, TimeSpan.Zero); // 12:00 IST

        ScheduleCalendar.NextRun(ScheduleFrequency.Daily, at0700, null, null, morning).ShouldBe(new DateTimeOffset(2026, 9, 15, 1, 30, 0, TimeSpan.Zero));
        ScheduleCalendar.NextRun(ScheduleFrequency.Daily, at0700, null, null, noon).ShouldBe(new DateTimeOffset(2026, 9, 16, 1, 30, 0, TimeSpan.Zero));

        // Exactly at the run time the next run is tomorrow (runs are strictly after "now").
        ScheduleCalendar.NextRun(ScheduleFrequency.Daily, at0700, null, null, new DateTimeOffset(2026, 9, 15, 1, 30, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 9, 16, 1, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Weekly_schedules_run_on_their_weekday()
    {
        var tuesdayNoon = new DateTimeOffset(2026, 9, 15, 6, 30, 0, TimeSpan.Zero);

        var next = ScheduleCalendar.NextRun(ScheduleFrequency.Weekly, new TimeOnly(8, 30), Weekday.Monday, null, tuesdayNoon);

        next.ShouldBe(new DateTimeOffset(2026, 9, 21, 3, 0, 0, TimeSpan.Zero));
        AnalyticsClock.LocalDate(next).DayOfWeek.ShouldBe(DayOfWeek.Monday);
        ScheduleCalendar.ToWeekday(DayOfWeek.Sunday).ShouldBe(Weekday.Sunday);
        ScheduleCalendar.ToWeekday(DayOfWeek.Monday).ShouldBe(Weekday.Monday);
    }

    [Fact]
    public void Monthly_schedules_use_the_last_day_when_the_month_is_shorter()
    {
        var february = new DateTimeOffset(2027, 2, 1, 6, 30, 0, TimeSpan.Zero);

        ScheduleCalendar.NextRun(ScheduleFrequency.Monthly, new TimeOnly(9, 0), null, ScheduleCalendar.LastDayOfMonth, february)
            .ShouldBe(AnalyticsClock.At(D("2027-02-28"), new TimeOnly(9, 0)));
        ScheduleCalendar.NextRun(ScheduleFrequency.Monthly, new TimeOnly(9, 0), null, 1, february)
            .ShouldBe(AnalyticsClock.At(D("2027-03-01"), new TimeOnly(9, 0)));
        ScheduleCalendar.NextRun(ScheduleFrequency.Monthly, new TimeOnly(9, 0), null, 15, new DateTimeOffset(2026, 9, 15, 3, 0, 0, TimeSpan.Zero))
            .ShouldBe(AnalyticsClock.At(D("2026-09-15"), new TimeOnly(9, 0)));
    }

    [Fact]
    public void Schedules_are_described_in_words()
    {
        ScheduleCalendar.Describe(ScheduleFrequency.Daily, new TimeOnly(7, 0), null, null).ShouldBe("Every day at 07:00 IST");
        ScheduleCalendar.Describe(ScheduleFrequency.Weekly, new TimeOnly(8, 30), Weekday.Friday, null).ShouldBe("Every Friday at 08:30 IST");
        ScheduleCalendar.Describe(ScheduleFrequency.Monthly, new TimeOnly(9, 0), null, 31).ShouldBe("On the last day of every month at 09:00 IST");
        ScheduleCalendar.Describe(ScheduleFrequency.Monthly, new TimeOnly(9, 0), null, 5).ShouldBe("On day 5 of every month at 09:00 IST");
    }

    [Fact]
    public void Hourly_extracts_run_at_their_minute_past_every_ist_hour()
    {
        var quarterPast = new TimeOnly(7, 15);

        // 12:00 IST → 12:15 IST; exactly at 12:15 → 13:15; 23:50 IST → 00:15 IST the next day.
        ScheduleCalendar.NextRun(ScheduleFrequency.Hourly, quarterPast, null, null, new DateTimeOffset(2026, 9, 15, 6, 30, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 9, 15, 6, 45, 0, TimeSpan.Zero));
        ScheduleCalendar.NextRun(ScheduleFrequency.Hourly, quarterPast, null, null, new DateTimeOffset(2026, 9, 15, 6, 45, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 9, 15, 7, 45, 0, TimeSpan.Zero));
        var next = ScheduleCalendar.NextRun(ScheduleFrequency.Hourly, quarterPast, null, null, new DateTimeOffset(2026, 9, 15, 18, 20, 0, TimeSpan.Zero));
        next.ShouldBe(new DateTimeOffset(2026, 9, 15, 18, 45, 0, TimeSpan.Zero));
        AnalyticsClock.LocalDate(next).ShouldBe(D("2026-09-16"));

        ScheduleCalendar.Describe(ScheduleFrequency.Hourly, quarterPast, null, null).ShouldBe("Every hour at 15 minutes past");
        ScheduleCalendar.Describe(ScheduleFrequency.Hourly, new TimeOnly(9, 0), null, null).ShouldBe("Every hour on the hour");
        ScheduleCalendar.Interval(ScheduleFrequency.Hourly).ShouldBe(TimeSpan.FromHours(1));
        ReportPeriods.DefaultFor(ScheduleFrequency.Hourly).ShouldBe(ReportPeriods.Today);
        ReportPeriods.ExtractDefault.ShouldBe(ReportPeriods.ThisFinancialYear);
    }

    [Fact]
    public void An_extract_is_late_once_it_misses_a_refresh_and_failing_says_why()
    {
        var now = new DateTimeOffset(2026, 9, 15, 6, 30, 0, TimeSpan.Zero);
        var extract = new ReportSchedule
        {
            Destination = ScheduleDestination.ExtractFolder,
            ExtractName = "daily-revenue",
            Frequency = ScheduleFrequency.Daily,
            IsActive = true,
            CreatedAt = now.AddDays(-3),
        };

        extract.State(now).ShouldBe(ExtractState.Late, "not produced in the three days since it was set up");

        extract.RecordRun(now.AddHours(-23), ScheduleRunStatus.Succeeded, null);
        extract.LastSucceededAt.ShouldBe(now.AddHours(-23));
        extract.State(now).ShouldBe(ExtractState.Fresh);
        extract.State(now.AddHours(1).AddMinutes(29)).ShouldBe(ExtractState.Fresh, "a day and 29 minutes after the refresh");
        extract.State(now.AddHours(1).AddMinutes(31)).ShouldBe(ExtractState.Late, "a day and 31 minutes after the refresh");

        extract.RecordRun(now, ScheduleRunStatus.Failed, "The disk is full.");
        extract.State(now).ShouldBe(ExtractState.Failing);
        extract.LastSucceededAt.ShouldBe(now.AddHours(-23), "a failed run leaves the data a BI tool reads as it was");

        extract.IsActive = false;
        extract.State(now).ShouldBe(ExtractState.Paused);

        new ReportSchedule { Destination = ScheduleDestination.ExtractFolder, Frequency = ScheduleFrequency.Hourly, IsActive = true, CreatedAt = now.AddMinutes(-10) }
            .State(now).ShouldBe(ExtractState.Waiting);
        var changed = new ReportSchedule
        {
            Destination = ScheduleDestination.ExtractFolder,
            Frequency = ScheduleFrequency.Daily,
            IsActive = true,
            CreatedAt = now.AddDays(-30),
            UpdatedAt = now.AddHours(-2),
        };
        changed.State(now).ShouldBe(ExtractState.Waiting, "changed two hours ago, and waiting for the refresh that writes the new file");
        new ReportSchedule { Frequency = ScheduleFrequency.Daily, IsActive = true, CreatedAt = now.AddDays(-9) }.IsLate(now).ShouldBeFalse("an e-mail is never late");
    }

    [Fact]
    public void A_paused_schedule_has_no_next_run()
    {
        var schedule = new ReportSchedule { Frequency = ScheduleFrequency.Daily, TimeOfDay = new TimeOnly(7, 0), IsActive = false };

        schedule.Reschedule(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));

        schedule.NextRunAt.ShouldBeNull();
        schedule.RecordRun(DateTimeOffset.UnixEpoch, ScheduleRunStatus.Failed, new string('x', 900));
        schedule.LastError!.Length.ShouldBe(500);
    }

    [Fact]
    public void Tat_percentiles_use_the_nearest_rank_and_ignore_bad_values()
    {
        var summary = Percentiles.Summarize([30, 10, 20, 40, 50, 60, 70, 80, 90, 100, -5, double.NaN], targetMinutes: 60);

        summary.Count.ShouldBe(10);
        summary.MedianMinutes.ShouldBe(50);
        summary.P90Minutes.ShouldBe(90);
        summary.AverageMinutes.ShouldBe(55);
        summary.WithinTargetPercent.ShouldBe(60);
        Percentiles.NearestRank([5, 7, 9], 50).ShouldBe(7);
        Percentiles.NearestRank([], 50).ShouldBeNull();
        Percentiles.Summarize([]).MedianMinutes.ShouldBeNull();
    }
}
