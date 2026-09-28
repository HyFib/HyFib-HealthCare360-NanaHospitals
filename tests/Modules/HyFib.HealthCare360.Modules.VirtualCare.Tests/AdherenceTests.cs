using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class AdherenceTests
{
    private static readonly DateOnly Today = new(2026, 9, 28);
    private static readonly DateOnly LongAgo = new(2026, 1, 1);

    private static IEnumerable<DateOnly> Days(int count, int step = 1) => Enumerable.Range(0, count).Select(i => Today.AddDays(-i * step));

    [Fact]
    public void A_reading_every_day_is_full_adherence()
    {
        var result = Adherence.Compute(Days(30), Today, LongAgo);

        result.WindowDays.ShouldBe(30);
        result.DaysWithReadings.ShouldBe(30);
        result.Percent.ShouldBe(100m);
        result.MissedDates.ShouldBeEmpty();
    }

    [Fact]
    public void Several_readings_on_one_day_count_once()
    {
        var result = Adherence.Compute([Today, Today, Today.AddDays(-1)], Today, LongAgo, windowDays: 10);

        result.DaysWithReadings.ShouldBe(2);
        result.Percent.ShouldBe(20m);
        result.MissedDates.Count.ShouldBe(8);
    }

    [Fact]
    public void Alternate_days_are_half_adherence_and_the_gaps_are_listed()
    {
        var result = Adherence.Compute(Days(15, step: 2), Today, LongAgo);

        result.Percent.ShouldBe(50m);
        result.MissedDates.ShouldContain(Today.AddDays(-1));
        result.MissedDates.ShouldNotContain(Today);
    }

    [Fact]
    public void The_window_starts_at_enrolment()
    {
        var result = Adherence.Compute(Days(4), Today, Today.AddDays(-4));

        result.WindowDays.ShouldBe(5);
        result.DaysWithReadings.ShouldBe(4);
        result.Percent.ShouldBe(80m);
    }

    [Fact]
    public void The_window_ends_with_the_enrolment()
    {
        var ended = Today.AddDays(-10);

        var result = Adherence.Compute([ended, Today], Today, LongAgo, windowDays: 7, endedOn: ended);

        result.WindowDays.ShouldBe(7);
        result.DaysWithReadings.ShouldBe(1);
        result.MissedDates.ShouldAllBe(d => d <= ended);
    }

    [Fact]
    public void Readings_outside_the_window_are_ignored() =>
        Adherence.Compute([Today.AddDays(-40), Today.AddDays(1)], Today, LongAgo).DaysWithReadings.ShouldBe(0);

    [Fact]
    public void A_future_enrolment_has_no_window() =>
        Adherence.Compute([], Today, Today.AddDays(3)).WindowDays.ShouldBe(0);

    [Fact]
    public void Percentages_are_rounded_to_one_decimal() =>
        Adherence.Compute(Days(1), Today, LongAgo, windowDays: 3).Percent.ShouldBe(33.3m);
}
