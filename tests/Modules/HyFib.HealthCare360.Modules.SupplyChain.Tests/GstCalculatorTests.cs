namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

public sealed class GstCalculatorTests
{
    [Fact]
    public void Intra_state_purchases_split_gst_equally_into_cgst_and_sgst()
    {
        var tax = GstCalculator.OnExclusive(1000m, 12m, interState: false);

        tax.ShouldBe(new TaxBreakup(1000m, 120m, 60m, 60m, 0m));
        tax.Total.ShouldBe(1120m);
    }

    [Fact]
    public void Inter_state_purchases_carry_igst_only()
    {
        GstCalculator.OnExclusive(1000m, 5m, interState: true).ShouldBe(new TaxBreakup(1000m, 50m, 0m, 0m, 50m));
    }

    [Fact]
    public void Mrp_inclusive_amounts_carve_out_the_tax()
    {
        GstCalculator.FromInclusive(112m, 12m).ShouldBe(new TaxBreakup(100m, 12m, 6m, 6m, 0m));
    }

    [Fact]
    public void Odd_paise_go_to_sgst_after_rounding_cgst_half_away_from_zero()
    {
        // 35.00 at 5%: taxable 33.33, GST 1.67 → CGST 0.84 (0.835 rounded away from zero), SGST 0.83.
        GstCalculator.FromInclusive(35m, 5m).ShouldBe(new TaxBreakup(33.33m, 1.67m, 0.84m, 0.83m, 0m));
    }

    [Fact]
    public void Purchase_line_applies_discount_before_gst()
    {
        var line = GstCalculator.PriceExclusive(10m, 45.50m, 10m, 5m, interState: false);

        line.Gross.ShouldBe(455m);
        line.Discount.ShouldBe(45.50m);
        line.Tax.Taxable.ShouldBe(409.50m);
        line.Tax.Gst.ShouldBe(20.48m);
        line.Net.ShouldBe(429.98m);
    }

    [Fact]
    public void Retail_line_prices_loose_units_at_mrp()
    {
        // A strip of 10 with MRP 35.00 sold as 10 loose tablets at 3.50 each.
        var line = GstCalculator.PriceInclusive(10m, 3.5m, 0m, 5m);

        line.Gross.ShouldBe(35m);
        line.Net.ShouldBe(35m);
        line.Tax.Taxable.ShouldBe(33.33m);
    }

    [Fact]
    public void Zero_rated_items_have_no_tax()
    {
        GstCalculator.FromInclusive(250m, 0m).ShouldBe(new TaxBreakup(250m, 0m, 0m, 0m, 0m));
    }

    [Theory]
    [InlineData(429.98, 430, 0.02)]
    [InlineData(100.50, 101, 0.50)]
    [InlineData(100.49, 100, -0.49)]
    public void Invoice_totals_round_to_the_rupee(double amount, double rounded, double roundOff)
    {
        var (total, difference) = GstCalculator.RoundToRupee((decimal)amount);

        total.ShouldBe((decimal)rounded);
        difference.ShouldBe((decimal)roundOff);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(18, true)]
    [InlineData(0.25, true)]
    [InlineData(7, false)]
    public void Only_notified_rates_are_accepted(double rate, bool expected) => GstCalculator.IsStandardRate((decimal)rate).ShouldBe(expected);
}
