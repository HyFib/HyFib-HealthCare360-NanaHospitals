using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests;

/// <summary>Turnaround-time figures reported for NABH quality indicators.</summary>
public sealed class TatStatisticsTests
{
    [Fact]
    public void Median_and_90th_percentile_are_observed_turnarounds()
    {
        // Nine routine results and one outsourced test: the mean is distorted, the median is not.
        double[] minutes = [45, 50, 38, 60, 42, 55, 48, 52, 40, 1440];

        var summary = TatStatistics.Summarize(minutes);

        summary.Count.ShouldBe(10);
        summary.AverageMinutes.ShouldBe(187.0);
        summary.MedianMinutes.ShouldBe(48.0);
        summary.P90Minutes.ShouldBe(60.0);
    }

    [Fact]
    public void Nothing_to_measure_gives_an_empty_summary() =>
        TatStatistics.Summarize([]).ShouldBe(new TatSummary(0, null, null, null));

    [Fact]
    public void Negative_and_invalid_durations_are_ignored() =>
        TatStatistics.Summarize([30, -5, double.NaN, double.PositiveInfinity]).ShouldBe(new TatSummary(1, 30.0, 30.0, 30.0));

    [Theory]
    [InlineData(50, 2.0)]
    [InlineData(90, 4.0)]
    [InlineData(100, 4.0)]
    [InlineData(1, 1.0)]
    public void Percentiles_use_the_nearest_rank(int percentile, double expected) =>
        TatStatistics.Percentile([1.0, 2.0, 3.0, 4.0], percentile).ShouldBe(expected);

    [Fact]
    public void Minutes_between_milestones_need_both_in_order()
    {
        TatStatistics.Minutes(Now, Now.AddMinutes(90)).ShouldBe(90.0);
        TatStatistics.Minutes(Now, null).ShouldBeNull();
        TatStatistics.Minutes(null, Now).ShouldBeNull();
        TatStatistics.Minutes(Now, Now.AddMinutes(-1)).ShouldBeNull();
    }
}
