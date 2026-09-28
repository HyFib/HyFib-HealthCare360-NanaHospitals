using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain;

public sealed class TimeAndCensusTests
{
    private static DateOnly D(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void Ist_days_start_at_1830_utc_the_evening_before()
    {
        AnalyticsClock.StartOfDay(D("2026-09-15")).ShouldBe(new DateTimeOffset(2026, 9, 14, 18, 30, 0, TimeSpan.Zero));
        AnalyticsClock.LocalDate(new DateTimeOffset(2026, 9, 14, 18, 29, 59, TimeSpan.Zero)).ShouldBe(D("2026-09-14"));
        AnalyticsClock.LocalDate(new DateTimeOffset(2026, 9, 14, 18, 30, 0, TimeSpan.Zero)).ShouldBe(D("2026-09-15"));
    }

    [Fact]
    public void A_date_range_is_a_half_open_utc_interval()
    {
        var (start, end) = AnalyticsClock.Range(D("2026-09-01"), D("2026-09-30"));

        start.ShouldBe(new DateTimeOffset(2026, 8, 31, 18, 30, 0, TimeSpan.Zero));
        end.ShouldBe(new DateTimeOffset(2026, 9, 30, 18, 30, 0, TimeSpan.Zero));
        AnalyticsClock.Days(D("2026-09-01"), D("2026-09-30")).ShouldBe(30);
        AnalyticsClock.Days(D("2026-09-02"), D("2026-09-01")).ShouldBe(0);
    }

    [Fact]
    public void Wall_clock_times_are_converted_from_ist()
    {
        AnalyticsClock.At(D("2026-09-15"), new TimeOnly(7, 0)).ShouldBe(new DateTimeOffset(2026, 9, 15, 1, 30, 0, TimeSpan.Zero));
        AnalyticsClock.FormatDateTime(new DateTimeOffset(2026, 9, 15, 1, 30, 0, TimeSpan.Zero)).ShouldBe("15 Sep 2026, 07:00 AM IST");
    }

    [Theory]
    [InlineData("2026-09-10", "2026-09-12", "2026-09-01", "2026-09-30", 2)]
    [InlineData("2026-09-10", "2026-09-10", "2026-09-01", "2026-09-30", 0)]
    [InlineData("2026-08-25", "2026-09-03", "2026-09-01", "2026-09-30", 2)]
    [InlineData("2026-09-28", null, "2026-09-01", "2026-09-30", 3)]
    [InlineData("2026-08-01", null, "2026-09-01", "2026-09-30", 30)]
    [InlineData("2026-10-02", null, "2026-09-01", "2026-09-30", 0)]
    [InlineData("2026-08-01", "2026-08-20", "2026-09-01", "2026-09-30", 0)]
    public void Census_days_count_the_midnights_a_patient_was_in(string admitted, string? discharged, string from, string to, int expected) =>
        CensusMath.CensusDays(D(admitted).DayNumber, discharged is null ? null : D(discharged).DayNumber, D(from).DayNumber, D(to).DayNumber).ShouldBe(expected);

    [Fact]
    public void Census_days_from_instants_use_ist_dates()
    {
        // Admitted 10 Sep 23:00 IST (17:30 UTC), discharged 12 Sep 00:30 IST (11 Sep 19:00 UTC): in at midnight 10→11 and 11→12.
        var admitted = new DateTimeOffset(2026, 9, 10, 17, 30, 0, TimeSpan.Zero);
        var discharged = new DateTimeOffset(2026, 9, 11, 19, 0, 0, TimeSpan.Zero);

        CensusMath.CensusDays(admitted, discharged, D("2026-09-01"), D("2026-09-30")).ShouldBe(2);
    }

    [Fact]
    public void Daily_midnight_census_matches_per_stay_counting()
    {
        var from = D("2026-09-01");
        var to = D("2026-09-10");
        var stays = new[]
        {
            new StaySpan(null, D("2026-08-30").DayNumber, D("2026-09-03").DayNumber),
            new StaySpan(null, D("2026-09-02").DayNumber, null),
            new StaySpan(null, D("2026-09-05").DayNumber, D("2026-09-05").DayNumber),
            new StaySpan(null, D("2026-09-09").DayNumber, D("2026-09-11").DayNumber),
        };

        var census = InpatientData.DailyCensus(stays, from, to);

        census.ShouldBe([1, 2, 1, 1, 1, 1, 1, 1, 2, 2]);
        census.Sum().ShouldBe(InpatientData.CensusDays(stays, from, to));
        CensusMath.InCensus(D("2026-09-02").DayNumber, null, D("2026-09-02").DayNumber).ShouldBeTrue();
        CensusMath.InCensus(D("2026-09-05").DayNumber, D("2026-09-05").DayNumber, D("2026-09-05").DayNumber).ShouldBeFalse();
    }

    [Fact]
    public void Trend_buckets_are_continuous_and_labelled()
    {
        Trend.Buckets(D("2026-09-01"), D("2026-09-03"), Trend.Day).ShouldBe([D("2026-09-01"), D("2026-09-02"), D("2026-09-03")]);

        // Weeks start on Monday: 1 Sep 2026 is a Tuesday.
        Trend.Buckets(D("2026-09-01"), D("2026-09-14"), Trend.Week).ShouldBe([D("2026-08-31"), D("2026-09-07"), D("2026-09-14")]);
        Trend.Buckets(D("2026-08-15"), D("2026-10-02"), Trend.Month).ShouldBe([D("2026-08-01"), D("2026-09-01"), D("2026-10-01")]);
        Trend.Label(D("2026-09-01"), Trend.Month).ShouldBe("Sep 2026");
        Trend.DaysIn(D("2026-08-31"), Trend.Week, D("2026-09-01"), D("2026-09-30")).ShouldBe(6);
    }
}
