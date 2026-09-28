using HyFib.HealthCare360.Modules.Billing.Domain;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class BillTotalsCalculatorTests
{
    private static BillTotals Calculate(BillDiscountInput discount, params BillLineInput[] lines) =>
        BillTotalsCalculator.Calculate(lines, discount, SplitTerms.SelfPay);

    [Theory]
    [InlineData(100.40, 50.35, 151, 0.25)]
    [InlineData(100.40, 50.09, 150, -0.49)]
    [InlineData(100.25, 50.25, 151, 0.50)]
    public void Patient_amount_is_rounded_to_the_nearest_rupee_half_away_from_zero(decimal first, decimal second, decimal expectedNet, decimal expectedRoundOff)
    {
        var totals = Calculate(BillDiscountInput.None, Line(first), Line(second));

        totals.PatientAmount.ShouldBe(expectedNet);
        totals.Net.ShouldBe(expectedNet);
        totals.RoundOff.ShouldBe(expectedRoundOff);
    }

    [Fact]
    public void Line_discount_can_never_exceed_the_line_value()
    {
        var totals = Calculate(BillDiscountInput.None, Line(100m, lineDiscount: 150m));

        totals.LineDiscount.ShouldBe(100m);
        totals.Net.ShouldBe(0m);
    }

    [Fact]
    public void Bill_percentage_discount_is_spread_in_proportion_to_line_values()
    {
        var totals = Calculate(new BillDiscountInput(10m, null), Line(300m), Line(100m));

        totals.BillDiscount.ShouldBe(40m);
        totals.Lines[0].BillDiscountShare.ShouldBe(30m);
        totals.Lines[1].BillDiscountShare.ShouldBe(10m);
        totals.Net.ShouldBe(360m);
    }

    [Fact]
    public void Rounding_remainder_of_a_bill_discount_goes_to_the_largest_line_so_the_total_is_exact()
    {
        var totals = Calculate(new BillDiscountInput(null, 100m), Line(100m), Line(100m), Line(100m));

        totals.Lines.Select(l => l.BillDiscountShare).ToArray().ShouldBe(new[] { 33.34m, 33.33m, 33.33m });
        totals.BillDiscount.ShouldBe(100m);
        totals.Net.ShouldBe(200m);
    }

    [Fact]
    public void Services_that_cannot_be_discounted_are_left_out_of_the_bill_discount()
    {
        var totals = Calculate(new BillDiscountInput(50m, null), Line(200m), Line(100m, discountAllowed: false, lineDiscount: 40m));

        totals.Lines[0].BillDiscountShare.ShouldBe(100m);
        totals.Lines[1].BillDiscountShare.ShouldBe(0m);
        totals.Lines[1].LineDiscount.ShouldBe(0m);
        totals.Net.ShouldBe(200m);
    }

    [Fact]
    public void Bill_discount_amount_is_capped_at_the_discountable_value()
    {
        var totals = Calculate(new BillDiscountInput(null, 1000m), Line(300m));

        totals.BillDiscount.ShouldBe(300m);
        totals.Net.ShouldBe(0m);
    }

    [Fact]
    public void Gst_is_charged_on_the_discounted_value_and_split_into_cgst_and_sgst()
    {
        var totals = Calculate(new BillDiscountInput(10m, null), Line(1000m, taxRate: 18m));

        totals.Taxable.ShouldBe(900m);
        totals.Tax.ShouldBe(162m);
        totals.Cgst.ShouldBe(81m);
        totals.Sgst.ShouldBe(81m);
        totals.Net.ShouldBe(1062m);
    }

    [Fact]
    public void Cgst_and_sgst_always_add_back_to_the_rounded_gst()
    {
        var totals = Calculate(BillDiscountInput.None, Line(100.05m, taxRate: 5m));

        totals.Tax.ShouldBe(5.00m);
        (totals.Cgst + totals.Sgst).ShouldBe(totals.Tax);

        var odd = Calculate(BillDiscountInput.None, Line(100.10m, taxRate: 5m));
        odd.Tax.ShouldBe(5.01m);
        odd.Cgst.ShouldBe(2.51m);
        odd.Sgst.ShouldBe(2.50m);
    }

    [Fact]
    public void Tariff_concession_applies_to_the_part_not_covered_by_a_package()
    {
        var totals = Calculate(BillDiscountInput.None, Line(1000m, covered: 600m, tariffPercent: 10m));

        totals.PackageCovered.ShouldBe(600m);
        totals.TariffDiscount.ShouldBe(40m);
        totals.Net.ShouldBe(360m);
    }

    [Fact]
    public void A_fully_package_covered_line_costs_nothing()
    {
        var totals = Calculate(BillDiscountInput.None, Line(1500m, covered: 1500m), Line(250m));

        totals.Lines[0].Net.ShouldBe(0m);
        totals.Net.ShouldBe(250m);
    }

    [Fact]
    public void User_discount_is_measured_against_the_value_after_tariff_concessions()
    {
        var totals = Calculate(new BillDiscountInput(null, 90m), Line(1000m, tariffPercent: 10m));

        totals.TariffDiscount.ShouldBe(100m);
        totals.UserDiscount.ShouldBe(90m);
        totals.DiscountBase.ShouldBe(900m);
        totals.TotalDiscount.ShouldBe(190m);
    }

    [Fact]
    public void An_empty_bill_totals_to_zero()
    {
        var totals = Calculate(new BillDiscountInput(10m, null));

        totals.Net.ShouldBe(0m);
        totals.RoundOff.ShouldBe(0m);
        totals.Lines.ShouldBeEmpty();
    }
}
