using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>HSN/SAC codes on canteen menu items, tax invoices and the day-end HSN summary (GSTR-1).</summary>
public sealed class CanteenTaxInvoiceTests
{
    private static readonly DateOnly Today = new(2026, 9, 29);

    private static MenuItem Tea() => new() { Name = "Tea", Price = 10m };

    private static MenuItem Meals() => new() { Name = "South Indian meals", Price = 80m };

    private static MenuItem Water() => new() { Name = "Water 1 L", Price = 20m, GstRate = 18m, HsnSac = "220190", TrackStock = true, StockOnHand = 40 };

    private static MenuItem WaterJar() => new() { Name = "Water jar 20 L", Price = 60m, GstRate = 12m, HsnSac = "220190" };

    private static MenuItem Coconut() => new() { Name = "Tender coconut", Price = 50m, GstRate = 0m, HsnSac = "080119" };

    private static SaveMenuItemRequest Request(string? hsnSac) =>
        new("WATER-1L", "Packaged drinking water 1 L", MenuCategory.Packaged, 20m, 18m, hsnSac, true, true, true, true, 10, null, null);

    private static CanteenSale Bill(params BillLineInput[] lines)
    {
        var sale = new CanteenSale { BusinessDate = Today };
        sale.ApplyTotals(CanteenBillCalculator.Calculate(lines));
        return sale;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2201")]
    [InlineData("220190")]
    [InlineData("22019090")]
    [InlineData(" 996333 ")]
    public void Hsn_and_sac_codes_are_optional_and_have_4_6_or_8_digits(string? code) =>
        new SaveMenuItemRequestValidator().Validate(Request(code)).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("22019")]
    [InlineData("2201909")]
    [InlineData("220190901")]
    [InlineData("99633A")]
    [InlineData("2201-90")]
    [InlineData("22 0190")]
    [InlineData("２２０１")]
    public void Other_hsn_and_sac_codes_are_refused(string code)
    {
        var result = new SaveMenuItemRequestValidator().Validate(Request(code));

        result.Errors.ShouldHaveSingleItem().PropertyName.ShouldBe(nameof(SaveMenuItemRequest.HsnSac));
        MenuItem.IsValidHsnSac(code).ShouldBeFalse();
    }

    [Fact]
    public void Food_made_in_the_canteen_is_billed_under_the_canteen_sac_and_goods_under_their_own_code()
    {
        MenuItem.DefaultSac.ShouldBe("996333");
        Tea().ToBillLine(2).HsnSac.ShouldBe(MenuItem.DefaultSac);
        new MenuItem { Name = "Idli", Price = 30m, HsnSac = " " }.ToBillLine(1).HsnSac.ShouldBe(MenuItem.DefaultSac);
        Water().ToBillLine(1).HsnSac.ShouldBe("220190");
    }

    [Fact]
    public void A_bill_keeps_the_names_rates_and_codes_it_was_made_with()
    {
        var tea = Tea();
        var water = Water();
        var sale = Bill(tea.ToBillLine(2), water.ToBillLine(1));

        tea.HsnSac = "996331";
        water.Name = "Mineral water 1 L";
        water.HsnSac = "22011010";
        water.GstRate = 5m;

        sale.Lines.Select(l => l.ItemName).ShouldBe(["Tea", "Water 1 L"]);
        sale.Lines.Select(l => l.HsnSac).ShouldBe(["996333", "220190"]);
        sale.Lines.Select(l => l.GstRate).ShouldBe([5m, 18m]);
        CanteenService.ToDto(sale).Lines.Select(l => l.HsnSac).ShouldBe(["996333", "220190"]);
    }

    [Fact]
    public void The_menu_item_dto_carries_the_items_own_code_only()
    {
        CanteenService.ToDto(Water()).HsnSac.ShouldBe("220190");
        CanteenService.ToDto(Tea()).HsnSac.ShouldBeNull();
    }

    [Fact]
    public void The_day_end_report_sums_completed_bills_by_code_and_gst_rate()
    {
        var tea = Tea();
        var meals = Meals();
        var water = Water();
        var jar = WaterJar();
        var coconut = Coconut();
        var cancelled = Bill(water.ToBillLine(3));
        cancelled.Status = CanteenSaleStatus.Cancelled;
        var beforeCodes = Bill(tea.ToBillLine(1));
        beforeCodes.Lines[0].HsnSac = null;
        var sales = new[] { Bill(tea.ToBillLine(2), water.ToBillLine(1)), Bill(meals.ToBillLine(1), coconut.ToBillLine(1), jar.ToBillLine(1)), cancelled, beforeCodes };
        var menu = new[] { tea, meals, water, jar, coconut }.ToDictionary(m => m.Id);

        var report = CanteenService.BuildDailySummary(Today, sales, menu);

        report.ByHsnSac.ShouldBe(
        [
            new HsnSacSummary("080119", 0m, 1, 50.00m, 0m, 0m, 50.00m),
            new HsnSacSummary("220190", 12m, 1, 53.57m, 3.22m, 3.21m, 60.00m),
            new HsnSacSummary("220190", 18m, 1, 16.95m, 1.53m, 1.52m, 20.00m),
            new HsnSacSummary("996333", 5m, 3, 95.24m, 2.39m, 2.37m, 100.00m),
            new HsnSacSummary(null, 5m, 1, 9.52m, 0.24m, 0.24m, 10.00m),
        ]);
        report.Bills.ShouldBe(3);
        report.CancelledBills.ShouldBe(1);
        report.ByHsnSac.Sum(h => h.TaxableAmount).ShouldBe(report.TaxableAmount);
        report.ByHsnSac.Sum(h => h.Cgst).ShouldBe(report.Cgst);
        report.ByHsnSac.Sum(h => h.Sgst).ShouldBe(report.Sgst);
        report.ByHsnSac.Sum(h => h.Total).ShouldBe(report.NetSales - report.RoundOff);
    }

    [Fact]
    public void A_day_without_sales_has_an_empty_hsn_summary() =>
        CanteenService.BuildDailySummary(Today, [], new Dictionary<Guid, MenuItem>()).ByHsnSac.ShouldBeEmpty();
}
