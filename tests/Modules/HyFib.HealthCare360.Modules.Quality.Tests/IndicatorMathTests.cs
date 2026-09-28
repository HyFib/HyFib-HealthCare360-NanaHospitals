using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

public sealed class IndicatorMathTests
{
    [Theory]
    [InlineData(IndicatorUnit.Percent, 100)]
    [InlineData(IndicatorUnit.PerThousand, 1000)]
    [InlineData(IndicatorUnit.Minutes, 1)]
    [InlineData(IndicatorUnit.Count, 1)]
    public void Multiplier_follows_the_unit(IndicatorUnit unit, double expected) =>
        IndicatorMath.DefaultMultiplier(unit).ShouldBe((decimal)expected);

    [Theory]
    [InlineData(3, 2400, 1000, 1.25)]
    [InlineData(45, 50, 100, 90)]
    [InlineData(1230, 60, 1, 20.5)]
    [InlineData(1, 3, 100, 33.33)]
    [InlineData(2, 3, 100, 66.67)]
    public void Value_is_numerator_over_denominator_scaled(double numerator, double denominator, double multiplier, double expected) =>
        IndicatorMath.Compute((decimal)numerator, (decimal)denominator, (decimal)multiplier).ShouldBe((decimal)expected);

    [Fact]
    public void A_zero_denominator_has_no_value() => IndicatorMath.Compute(5, 0, 100).ShouldBeNull();

    [Theory]
    [InlineData(92, 90, IndicatorDirection.HigherIsBetter, true)]
    [InlineData(88, 90, IndicatorDirection.HigherIsBetter, false)]
    [InlineData(0.8, 1, IndicatorDirection.LowerIsBetter, true)]
    [InlineData(1.2, 1, IndicatorDirection.LowerIsBetter, false)]
    [InlineData(1, 1, IndicatorDirection.LowerIsBetter, true)]
    public void Target_is_judged_in_the_indicators_direction(double value, double target, IndicatorDirection direction, bool expected) =>
        IndicatorMath.MeetsTarget((decimal)value, (decimal)target, direction).ShouldBe(expected);

    [Fact]
    public void Without_a_target_there_is_no_verdict() => IndicatorMath.MeetsTarget(5, null, IndicatorDirection.LowerIsBetter).ShouldBeNull();

    [Theory]
    [InlineData("2026-09-17", IndicatorFrequency.Monthly, "2026-09-01")]
    [InlineData("2026-05-20", IndicatorFrequency.Quarterly, "2026-04-01")]
    [InlineData("2026-12-31", IndicatorFrequency.Quarterly, "2026-10-01")]
    [InlineData("2026-02-10", IndicatorFrequency.Annually, "2025-04-01")]
    [InlineData("2026-04-01", IndicatorFrequency.Annually, "2026-04-01")]
    public void Dates_normalise_to_the_start_of_their_period(string date, IndicatorFrequency frequency, string expected) =>
        IndicatorMath.PeriodStart(DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), frequency)
            .ShouldBe(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Periods_have_readable_labels()
    {
        IndicatorMath.PeriodLabel(new DateOnly(2026, 9, 1), IndicatorFrequency.Monthly).ShouldBe("Sep 2026");
        IndicatorMath.PeriodLabel(new DateOnly(2026, 7, 1), IndicatorFrequency.Quarterly).ShouldBe("Q3 2026");
        IndicatorMath.PeriodLabel(new DateOnly(2025, 4, 1), IndicatorFrequency.Annually).ShouldBe("FY 2025-26");
    }

    [Fact]
    public void Trend_needs_at_least_two_values()
    {
        IndicatorMath.Trend([], IndicatorDirection.LowerIsBetter).ShouldBe(TrendDirection.InsufficientData);
        IndicatorMath.Trend([1.5m], IndicatorDirection.LowerIsBetter).ShouldBe(TrendDirection.InsufficientData);
    }

    [Fact]
    public void Falling_fall_rates_are_improving()
    {
        IndicatorMath.Trend([1.6m, 1.5m, 1.4m, 0.9m], IndicatorDirection.LowerIsBetter).ShouldBe(TrendDirection.Improving);
        IndicatorMath.Trend([1.6m, 1.5m, 1.4m, 0.9m], IndicatorDirection.HigherIsBetter).ShouldBe(TrendDirection.Worsening);
    }

    [Fact]
    public void Falling_hand_hygiene_compliance_is_worsening()
    {
        IndicatorMath.Trend([92m, 91m, 90m, 80m], IndicatorDirection.HigherIsBetter).ShouldBe(TrendDirection.Worsening);
    }

    [Fact]
    public void Small_changes_are_stable()
    {
        IndicatorMath.Trend([90m, 91m, 92m, 92.5m], IndicatorDirection.HigherIsBetter).ShouldBe(TrendDirection.Stable);
        IndicatorMath.Trend([0m, 0m], IndicatorDirection.LowerIsBetter).ShouldBe(TrendDirection.Stable);
    }

    [Fact]
    public void Only_the_three_previous_values_form_the_baseline()
    {
        // An old spike (10) does not make today's 2.0 look like an improvement against 2.0, 2.0, 2.0.
        IndicatorMath.Trend([10m, 2m, 2m, 2m, 2m], IndicatorDirection.LowerIsBetter).ShouldBe(TrendDirection.Stable);
    }
}
