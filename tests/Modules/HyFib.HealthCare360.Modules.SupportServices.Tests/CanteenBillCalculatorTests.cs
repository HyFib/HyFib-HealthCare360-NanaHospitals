using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

public sealed class CanteenBillCalculatorTests
{
    private static BillLineInput Line(string name, int quantity, decimal price, decimal gstRate = 5m, bool inclusive = true) =>
        new(Guid.NewGuid(), name, quantity, price, gstRate, inclusive, MenuItem.DefaultSac);

    [Fact]
    public void Gst_is_backed_out_of_inclusive_prices_and_split_into_cgst_and_sgst()
    {
        var bill = CanteenBillCalculator.Calculate([Line("Tea", 2, 10m)]);

        bill.TaxableAmount.ShouldBe(19.05m);
        bill.TotalGst.ShouldBe(0.95m);
        bill.Cgst.ShouldBe(0.48m);
        bill.Sgst.ShouldBe(0.47m);
        bill.GrossAmount.ShouldBe(20.00m);
        bill.RoundOff.ShouldBe(0m);
        bill.NetAmount.ShouldBe(20m);
    }

    [Fact]
    public void Gst_is_added_to_exclusive_prices()
    {
        var bill = CanteenBillCalculator.Calculate([Line("Catering tray", 1, 100m, inclusive: false)]);

        bill.TaxableAmount.ShouldBe(100m);
        bill.Cgst.ShouldBe(2.50m);
        bill.Sgst.ShouldBe(2.50m);
        bill.NetAmount.ShouldBe(105m);
    }

    [Fact]
    public void The_bill_is_rounded_to_the_nearest_rupee_with_a_round_off_line()
    {
        var bill = CanteenBillCalculator.Calculate([Line("Meals", 1, 47m, inclusive: false)]);

        bill.Cgst.ShouldBe(1.18m);
        bill.Sgst.ShouldBe(1.17m);
        bill.GrossAmount.ShouldBe(49.35m);
        bill.RoundOff.ShouldBe(-0.35m);
        bill.NetAmount.ShouldBe(49m);
    }

    [Fact]
    public void Fifty_paise_rounds_up()
    {
        var bill = CanteenBillCalculator.Calculate([Line("Snack", 1, 10m, inclusive: false)]);

        bill.GrossAmount.ShouldBe(10.50m);
        bill.RoundOff.ShouldBe(0.50m);
        bill.NetAmount.ShouldBe(11m);
    }

    [Fact]
    public void Each_line_is_taxed_at_its_own_rate()
    {
        var bill = CanteenBillCalculator.Calculate([Line("Water 1 L", 1, 20m, gstRate: 18m), Line("Tender coconut", 1, 50m, gstRate: 0m)]);

        bill.Lines[0].TaxableAmount.ShouldBe(16.95m);
        bill.Lines[0].Cgst.ShouldBe(1.53m);
        bill.Lines[0].Sgst.ShouldBe(1.52m);
        bill.Lines[1].TaxableAmount.ShouldBe(50m);
        bill.Lines[1].Cgst.ShouldBe(0m);
        bill.TaxableAmount.ShouldBe(66.95m);
        bill.GrossAmount.ShouldBe(70m);
    }

    [Fact]
    public void Line_totals_add_up_to_the_gross_amount()
    {
        var bill = CanteenBillCalculator.Calculate([Line("Idli", 3, 30m), Line("Coffee", 2, 15m), Line("Samosa", 4, 15m)]);

        bill.Lines.Sum(l => l.LineTotal).ShouldBe(bill.GrossAmount);
        bill.Lines.Sum(l => l.TaxableAmount + l.Cgst + l.Sgst).ShouldBe(bill.GrossAmount);
        bill.GrossAmount.ShouldBe(180m);
    }

    [Fact]
    public void Quantities_must_be_positive() =>
        Should.Throw<ArgumentOutOfRangeException>(() => CanteenBillCalculator.Calculate([Line("Tea", 0, 10m)]));

    [Fact]
    public void Tracked_stock_cannot_go_negative_but_untracked_items_sell_freely()
    {
        var water = new MenuItem { Name = "Water 1 L", TrackStock = true, StockOnHand = 2 };
        var tea = new MenuItem { Name = "Tea", TrackStock = false };

        water.Consume(3).Error!.Code.ShouldBe("canteen.out_of_stock");
        water.Consume(2).IsSuccess.ShouldBeTrue();
        water.StockOnHand.ShouldBe(0);
        tea.Consume(500).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Bills_are_cancelled_only_on_their_own_business_day()
    {
        var today = new DateOnly(2026, 9, 25);
        var now = new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
        var yesterdays = new CanteenSale { BusinessDate = today.AddDays(-1) };
        var todays = new CanteenSale { BusinessDate = today };

        yesterdays.Cancel("Wrong item", null, today, now).Error!.Code.ShouldBe("canteen.day_closed");
        todays.Cancel("Wrong item", null, today, now).IsSuccess.ShouldBeTrue();
        todays.Status.ShouldBe(CanteenSaleStatus.Cancelled);
        todays.Cancel("again", null, today, now).Error!.Code.ShouldBe("canteen.sale_cancelled");
    }
}
