using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Services;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain;

/// <summary>KPI targets: proration, status against the thresholds, when owners are told, and the threshold order.</summary>
public sealed class KpiTargetMathTests
{
    [Theory]
    [InlineData(3_000_000, 10, 986_301.37)]
    [InlineData(3_000_000, 365, 36_000_000)]
    [InlineData(3_000, 28, 2_761.64)]
    [InlineData(3_000, 31, 3_057.53)]
    [InlineData(3_000, 1, 98.63)]
    [InlineData(3_000, 0, 0)]
    public void A_monthly_target_is_prorated_at_twelve_months_a_year(double monthly, int days, double expected) =>
        KpiTargetMath.Prorate((decimal)monthly, days).ShouldBe((decimal)expected);

    [Fact]
    public void Counts_and_money_are_prorated_and_rates_are_compared_as_they_are()
    {
        KpiTargetMath.ForPeriod("inr", 3_000_000m, 2_700_000m, 2_400_000m, 10).ShouldBe(new KpiTargetBand(986_301.37m, 887_671.23m, 789_041.10m));
        KpiTargetMath.ForPeriod("count", 3_000m, 2_500m, 2_000m, 28).ShouldBe(new KpiTargetBand(2_761.64m, 2_301.37m, 1_841.10m));
        KpiTargetMath.ForPeriod("percent", 85m, 80m, 70m, 28).ShouldBe(new KpiTargetBand(85m, 80m, 70m));
        KpiTargetMath.ForPeriod("days", 4m, 5m, 6m, 365).ShouldBe(new KpiTargetBand(4m, 5m, 6m));
    }

    [Theory]
    [InlineData(92, "Green")]
    [InlineData(90, "Green")]
    [InlineData(85, "Amber")]
    [InlineData(80.01, "Amber")]
    [InlineData(80, "Red")]
    [InlineData(10, "Red")]
    public void Where_higher_is_better_the_value_is_green_at_or_above_amber_and_red_at_or_below_red(double value, string expected) =>
        KpiTargetMath.Status((decimal)value, new KpiTargetBand(95m, 90m, 80m), higherIsBetter: true).ShouldBe(Enum.Parse<KpiStatus>(expected));

    [Theory]
    [InlineData(4.8, "Green")]
    [InlineData(5, "Green")]
    [InlineData(5.5, "Amber")]
    [InlineData(6, "Red")]
    [InlineData(9, "Red")]
    public void Where_lower_is_better_the_value_is_green_at_or_below_amber_and_red_at_or_above_red(double value, string expected) =>
        KpiTargetMath.Status((decimal)value, new KpiTargetBand(4m, 5m, 6m), higherIsBetter: false).ShouldBe(Enum.Parse<KpiStatus>(expected));

    [Fact]
    public void A_kpi_without_a_figure_has_no_status() =>
        KpiTargetMath.Status(null, new KpiTargetBand(95m, 90m, 80m), true).ShouldBeNull();

    [Theory]
    [InlineData(null, "Amber", true)]
    [InlineData(null, "Red", true)]
    [InlineData("Green", "Amber", true)]
    [InlineData("Amber", "Red", true)]
    [InlineData("Green", "Red", true)]
    [InlineData("Amber", "Amber", false)]
    [InlineData("Red", "Red", false)]
    [InlineData("Red", "Amber", false)]
    [InlineData("Amber", "Green", false)]
    [InlineData(null, "Green", false)]
    [InlineData("Red", null, false)]
    public void Owners_are_told_when_the_kpi_gets_worse_and_not_again_while_it_stays(string? previous, string? current, bool told) =>
        KpiTargetMath.IsNewBreach(Parse(previous), Parse(current)).ShouldBe(told);

    [Fact]
    public void Thresholds_lie_on_the_bad_side_of_the_target_red_beyond_amber()
    {
        KpiTargetMath.CheckOrder(95m, 90m, 80m, higherIsBetter: true).ShouldBeNull();
        KpiTargetMath.CheckOrder(95m, 95m, 95m, higherIsBetter: true).ShouldBeNull();
        KpiTargetMath.CheckOrder(95m, 96m, 80m, higherIsBetter: true)!.Value.Field.ShouldBe("amberThreshold");
        KpiTargetMath.CheckOrder(95m, 90m, 91m, higherIsBetter: true)!.Value.Field.ShouldBe("redThreshold");
        KpiTargetMath.CheckOrder(4m, 5m, 6m, higherIsBetter: false).ShouldBeNull();
        KpiTargetMath.CheckOrder(4m, 3m, 6m, higherIsBetter: false)!.Value.Field.ShouldBe("amberThreshold");
        KpiTargetMath.CheckOrder(4m, 5m, 4.5m, higherIsBetter: false)!.Value.Field.ShouldBe("redThreshold");
    }

    [Fact]
    public void The_target_view_prorates_counts_and_gives_the_daily_target_for_the_trend()
    {
        var target = new KpiTarget { KpiKey = "revenue", Target = 3_000_000m, AmberThreshold = 2_700_000m, RedThreshold = 2_400_000m, OwnerRole = "Accountant" };

        var view = KpiService.TargetView("inr", higherIsBetter: true, 900_000m, target, 10);

        view.ShouldBe(new KpiTargetView(986_301.37m, 887_671.23m, 789_041.10m, true, KpiStatus.Green, "Accountant", 98_630.14m));
        KpiService.TargetView("inr", true, 800_000m, target, 10).Status.ShouldBe(KpiStatus.Amber);
        KpiService.TargetView("inr", true, 789_041.10m, target, 10).Status.ShouldBe(KpiStatus.Red);

        var occupancy = new KpiTarget { KpiKey = "occupancy", Target = 85m, AmberThreshold = 80m, RedThreshold = 70m, OwnerRole = "HospitalAdmin" };
        KpiService.TargetView("percent", true, 72.5m, occupancy, 30).ShouldBe(new KpiTargetView(85m, 80m, 70m, false, KpiStatus.Amber, "HospitalAdmin", null));
    }

    [Fact]
    public void The_breach_notice_gives_the_figure_against_the_target_in_the_kpis_unit()
    {
        var revenue = KpiDefinitions.Revenue;
        var (title, body) = KpiTargetMonitor.Message(revenue, 700_000m, new KpiTargetView(986_301.37m, 887_671.23m, 789_041.10m, true, KpiStatus.Red, "Accountant", 98_630.14m));

        title.ShouldBe("Revenue is red");
        body.ShouldBe("Month to date: ₹7,00,000.00 against a target of ₹9,86,301.37 (the monthly target prorated to the month so far); red at ₹7,89,041.10 or below.");

        var (alosTitle, alosBody) = KpiTargetMonitor.Message(KpiDefinitions.Alos, 5.5m, new KpiTargetView(4m, 5m, 6m, false, KpiStatus.Amber, "HospitalAdmin", null));
        alosTitle.ShouldBe("Average length of stay is amber");
        alosBody.ShouldBe("Month to date: 5.5 days against a target of 4 days; amber at 5 days or above.");
    }

    private static KpiStatus? Parse(string? value) => value is null ? null : Enum.Parse<KpiStatus>(value);
}
