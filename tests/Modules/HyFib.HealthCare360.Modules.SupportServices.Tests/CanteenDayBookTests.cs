using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Services;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>The canteen's day as it reaches Billing's Tally day book.</summary>
public sealed class CanteenDayBookTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static CanteenSale Sale(CanteenPaymentMode mode, params (int Quantity, decimal Price, decimal GstRate)[] lines)
    {
        var sale = new CanteenSale { BusinessDate = Today, PaymentMode = mode };
        sale.ApplyTotals(CanteenBillCalculator.Calculate(
            lines.Select(l => new BillLineInput(Guid.NewGuid(), "Item", l.Quantity, l.Price, l.GstRate, true, MenuItem.DefaultSac)).ToList()));
        return sale;
    }

    [Fact]
    public void Completed_bills_are_summed_by_gst_rate_and_staff_credit_goes_on_account()
    {
        var cash = Sale(CanteenPaymentMode.Cash, (2, 10m, 5m));
        var upi = Sale(CanteenPaymentMode.Upi, (1, 20m, 18m), (1, 50m, 0m));
        var staff = Sale(CanteenPaymentMode.StaffCredit, (3, 10m, 5m));
        var cancelled = Sale(CanteenPaymentMode.Cash, (5, 10m, 5m));
        cancelled.Cancel("Wrong order", null, Today, DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();

        var day = CanteenDayBook.Day(Today, [cash, upi, staff, cancelled]);

        day.Counter.ShouldBe("canteen");
        day.Sales.Count.ShouldBe(3);
        day.Sales.Tax.Select(t => t.GstRate).ShouldBe([0m, 5m, 18m]);
        day.Sales.Tax.Single(t => t.GstRate == 5m).Total.ShouldBe(50m);
        day.Sales.Payments.ShouldBe([new CounterMoney(CounterPaymentMode.Cash, 20m), new CounterMoney(CounterPaymentMode.Upi, 70m)]);
        day.Sales.Total.ShouldBe(cash.NetAmount + upi.NetAmount + staff.NetAmount);
        day.Sales.OnAccount.ShouldBe(staff.NetAmount);
        day.Returns.ShouldBe(CounterDocuments.None);
        day.Collected.ShouldBeEmpty();
    }

    [Fact]
    public void A_day_without_bills_is_empty() => CanteenDayBook.Day(Today, []).IsEmpty.ShouldBeTrue();
}
