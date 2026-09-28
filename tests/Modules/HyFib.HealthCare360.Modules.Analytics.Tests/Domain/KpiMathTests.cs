using HyFib.HealthCare360.Modules.Analytics.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain;

public sealed class KpiMathTests
{
    [Theory]
    [InlineData(2_400, 100, 30, 80.0)]
    [InlineData(3_100, 100, 31, 100.0)]
    [InlineData(1, 3, 1, 33.3)]
    [InlineData(0, 50, 30, 0.0)]
    public void Occupancy_is_inpatient_days_over_available_bed_days(int inpatientDays, int beds, int days, double expected) =>
        KpiMath.OccupancyPercent(inpatientDays, beds, days).ShouldBe((decimal)expected);

    [Fact]
    public void Occupancy_is_blank_without_beds_or_days()
    {
        KpiMath.OccupancyPercent(10, 0, 30).ShouldBeNull();
        KpiMath.OccupancyPercent(10, 10, 0).ShouldBeNull();
    }

    [Theory]
    [InlineData("2026-09-10", "2026-09-12", 2)]
    [InlineData("2026-09-10", "2026-09-10", 1)]
    [InlineData("2026-08-31", "2026-09-01", 1)]
    [InlineData("2026-02-27", "2026-03-02", 3)]
    public void Length_of_stay_counts_calendar_days_with_a_minimum_of_one(string admitted, string discharged, int expected) =>
        KpiMath.LengthOfStayDays(DateOnly.Parse(admitted, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(discharged, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);

    [Fact]
    public void Alos_is_total_stay_over_discharges_to_one_decimal()
    {
        KpiMath.AverageLengthOfStay(38, 10).ShouldBe(3.8m);
        KpiMath.AverageLengthOfStay(10, 3).ShouldBe(3.3m);
        KpiMath.AverageLengthOfStay(11, 3).ShouldBe(3.7m);
        KpiMath.AverageLengthOfStay(0, 0).ShouldBeNull();
    }

    [Fact]
    public void Bed_turnover_rate_is_discharges_per_bed()
    {
        KpiMath.BedTurnoverRate(300, 100).ShouldBe(3m);
        KpiMath.BedTurnoverRate(10, 3).ShouldBe(3.33m);
        KpiMath.BedTurnoverRate(10, 0).ShouldBeNull();
    }

    [Fact]
    public void Bed_turnover_interval_is_idle_bed_days_per_discharge_and_never_negative()
    {
        // 100 beds × 30 days = 3,000 bed-days; 2,400 used; 300 discharges → 2 idle days between patients.
        KpiMath.BedTurnoverInterval(100, 30, 2_400, 300).ShouldBe(2m);
        KpiMath.BedTurnoverInterval(10, 30, 320, 40).ShouldBe(0m);
        KpiMath.BedTurnoverInterval(10, 30, 200, 0).ShouldBeNull();
    }

    [Fact]
    public void Nps_is_percent_promoters_minus_percent_detractors()
    {
        KpiMath.NetPromoterScore(70, 20, 10).ShouldBe(60m);
        KpiMath.NetPromoterScore(1, 1, 1).ShouldBe(0m);
        KpiMath.NetPromoterScore(0, 0, 5).ShouldBe(-100m);
        KpiMath.NetPromoterScore(2, 0, 1).ShouldBe(33.3m);
        KpiMath.NetPromoterScore(0, 0, 0).ShouldBeNull();
    }

    [Fact]
    public void Death_rates_follow_the_gross_and_net_definitions()
    {
        KpiMath.GrossDeathRate(5, 400).ShouldBe(1.25m);

        // Net death rate excludes deaths within 48 hours from both numerator and denominator.
        KpiMath.NetDeathRate(5, 2, 400).ShouldBe(0.75m);
        KpiMath.GrossDeathRate(0, 0).ShouldBeNull();
    }

    [Fact]
    public void No_show_rate_is_measured_against_appointments_that_were_due()
    {
        KpiMath.NoShowRate(10, 90).ShouldBe(10m);
        KpiMath.NoShowRate(0, 0).ShouldBeNull();
    }

    [Fact]
    public void Change_percent_compares_with_the_previous_period()
    {
        KpiMath.ChangePercent(120, 100).ShouldBe(20m);
        KpiMath.ChangePercent(80, 100).ShouldBe(-20m);
        KpiMath.ChangePercent(10, -20).ShouldBe(150m);
        KpiMath.ChangePercent(10, 0).ShouldBeNull();
        KpiMath.ChangePercent(null, 10).ShouldBeNull();
    }

    [Fact]
    public void Rates_per_thousand_and_margins()
    {
        KpiMath.PerThousand(3, 1_500).ShouldBe(2m);
        KpiMath.PerThousand(3, 0).ShouldBeNull();
        KpiMath.MarginPercent(1_000, 750).ShouldBe(25m);
        KpiMath.MarginPercent(0, 10).ShouldBeNull();
        KpiMath.Percent(1, 3).ShouldBe(33.3m);
        KpiMath.Percent(2, 3).ShouldBe(66.7m);
    }
}
