using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>The pharmacy's day as it reaches Billing's Tally day book.</summary>
public sealed class PharmacyDayBookTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static PharmacySale Sale(string number, decimal mrp, decimal quantity, decimal gstRate, params SalePayment[] payments)
    {
        var sale = new PharmacySale { InvoiceNumber = number, SoldAt = Now, PatientId = Guid.CreateVersion7() };
        var item = new PharmacySaleItem { ItemName = "Medicine", BatchNumber = "B1", UnitMrp = mrp, Quantity = quantity, GstRate = gstRate };
        item.Price(0);
        sale.Items.Add(item);
        sale.Complete(payments, creditAllowed: true, Now, null).IsSuccess.ShouldBeTrue();
        return sale;
    }

    [Fact]
    public void Invoices_returns_and_later_collections_add_up_to_what_the_counter_took()
    {
        var credit = Sale("NHPH260000001", 2.25m, 20, 5m, new(PaymentMode.Cash, 20m, null), new(PaymentMode.Credit, 25m, null));
        credit.CollectPayment(PaymentMode.Card, 25m, "SLIP9", Now.AddHours(2), null).IsSuccess.ShouldBeTrue();
        var paid = Sale("NHPH260000002", 112m, 1, 12m, new SalePayment(PaymentMode.Upi, 112m, "UPI1"));
        var cancelled = Sale("NHPH260000003", 50m, 1, 12m, new SalePayment(PaymentMode.Cash, 50m, null));
        cancelled.Cancel("Wrong patient", Today, Today, Now.AddMinutes(5), null).IsSuccess.ShouldBeTrue();
        var refund = paid.Items[0].Return(1).Value;
        var applied = paid.ApplyReturn(refund, PaymentMode.Cash, null, Now.AddHours(3), null).Value;
        var saleReturn = new PharmacySaleReturn
        {
            ReturnNumber = "NHPR260000001",
            SaleId = paid.Id,
            RefundAmount = refund,
            AdjustedAgainstDue = applied.AdjustedAgainstDue,
            CashRefund = applied.CashRefund,
            RefundMode = PaymentMode.Cash,
            Items = [new PharmacySaleReturnItem { SaleItemId = paid.Items[0].Id, RefundAmount = refund, Quantity = 1 }],
        };

        var day = PharmacyDayBook.Day(Today, [credit, paid, cancelled], [saleReturn], new Dictionary<Guid, decimal> { [paid.Items[0].Id] = 12m },
            [new PharmacyCollection(PaymentMode.Card, 25m)]);

        day.Counter.ShouldBe("pharmacy");
        day.Sales.Count.ShouldBe(2);
        day.Sales.Tax.ShouldBe([new CounterTaxLine(5m, 42.86m, 1.07m, 1.07m), new CounterTaxLine(12m, 100m, 6m, 6m)]);
        day.Sales.Payments.ShouldBe([new CounterMoney(CounterPaymentMode.Cash, 20m), new CounterMoney(CounterPaymentMode.Upi, 112m)]);
        day.Sales.OnAccount.ShouldBe(25m);
        day.Sales.Total.ShouldBe(157m);

        day.Returns.Count.ShouldBe(1);
        day.Returns.Tax.ShouldBe([new CounterTaxLine(12m, 100m, 6m, 6m)]);
        day.Returns.Payments.ShouldBe([new CounterMoney(CounterPaymentMode.Cash, 112m)]);
        day.Returns.OnAccount.ShouldBe(0m);

        day.Collected.ShouldBe([new CounterMoney(CounterPaymentMode.Card, 25m)]);
    }

    [Fact]
    public void A_return_set_against_a_balance_due_comes_off_the_account()
    {
        var credit = Sale("NHPH260000001", 2.25m, 20, 5m, new SalePayment(PaymentMode.Credit, 45m, null));
        var refund = credit.Items[0].Return(4).Value;
        var applied = credit.ApplyReturn(refund, PaymentMode.Cash, null, Now, null).Value;
        var saleReturn = new PharmacySaleReturn
        {
            RefundAmount = refund,
            AdjustedAgainstDue = applied.AdjustedAgainstDue,
            CashRefund = applied.CashRefund,
            RefundMode = PaymentMode.Cash,
            Items = [new PharmacySaleReturnItem { SaleItemId = credit.Items[0].Id, RefundAmount = refund }],
        };

        var day = PharmacyDayBook.Day(Today, [], [saleReturn], new Dictionary<Guid, decimal> { [credit.Items[0].Id] = 5m }, []);

        day.Returns.Total.ShouldBe(9m);
        day.Returns.Payments.ShouldBeEmpty();
        day.Returns.OnAccount.ShouldBe(9m);
    }

    [Fact]
    public void A_quiet_day_is_empty() =>
        PharmacyDayBook.Day(Today, [], [], new Dictionary<Guid, decimal>(), []).IsEmpty.ShouldBeTrue();
}
