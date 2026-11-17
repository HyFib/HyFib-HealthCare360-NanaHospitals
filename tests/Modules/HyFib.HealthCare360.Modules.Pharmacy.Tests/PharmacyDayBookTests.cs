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
        var applied = paid.ApplyReturn(refund, Now.AddHours(3), null).Value;
        var saleReturn = new PharmacySaleReturn
        {
            ReturnNumber = "NHPR260000001",
            SaleId = paid.Id,
            RefundAmount = refund,
            AdjustedAgainstDue = applied.AdjustedAgainstDue,
            CashRefund = applied.PaidBack.Sum(p => p.Amount),
            RefundMode = PaymentMode.Upi,
            Items = [new PharmacySaleReturnItem { SaleItemId = paid.Items[0].Id, RefundAmount = refund, Quantity = 1 }],
        };

        // The invoice paid by UPI is paid back by UPI: the refunds of the day, as the source reads them.
        var day = PharmacyDayBook.Day(Today, [credit, paid, cancelled], [saleReturn], new Dictionary<Guid, decimal> { [paid.Items[0].Id] = 12m },
            [new PharmacyCollection(PaymentMode.Card, 25m)], paidBack: [.. paid.Payments.Where(p => p.IsRefund).Select(p => new PharmacyCollection(p.Mode, p.Amount))]);

        day.Counter.ShouldBe("pharmacy");
        day.Sales.Count.ShouldBe(2);
        day.Sales.Tax.ShouldBe([new CounterTaxLine(5m, 42.86m, 1.07m, 1.07m), new CounterTaxLine(12m, 100m, 6m, 6m)]);
        day.Sales.Payments.ShouldBe([new CounterMoney(CounterPaymentMode.Cash, 20m), new CounterMoney(CounterPaymentMode.Upi, 112m)]);
        day.Sales.OnAccount.ShouldBe(25m);
        day.Sales.Total.ShouldBe(157m);

        day.Returns.Count.ShouldBe(1);
        day.Returns.Tax.ShouldBe([new CounterTaxLine(12m, 100m, 6m, 6m)]);
        day.Returns.Payments.ShouldBe([new CounterMoney(CounterPaymentMode.Upi, 112m)]);
        day.Returns.OnAccount.ShouldBe(0m);

        day.Collected.ShouldBe([new CounterMoney(CounterPaymentMode.Card, 25m)]);
    }

    [Fact]
    public void A_return_set_against_a_balance_due_comes_off_the_account()
    {
        var credit = Sale("NHPH260000001", 2.25m, 20, 5m, new SalePayment(PaymentMode.Credit, 45m, null));
        var refund = credit.Items[0].Return(4).Value;
        var applied = credit.ApplyReturn(refund, Now, null).Value;
        var saleReturn = new PharmacySaleReturn
        {
            RefundAmount = refund,
            AdjustedAgainstDue = applied.AdjustedAgainstDue,
            CashRefund = applied.PaidBack.Sum(p => p.Amount),
            RefundMode = PaymentMode.Cash,
            Items = [new PharmacySaleReturnItem { SaleItemId = credit.Items[0].Id, RefundAmount = refund }],
        };

        var day = PharmacyDayBook.Day(Today, [], [saleReturn], new Dictionary<Guid, decimal> { [credit.Items[0].Id] = 5m }, []);

        day.Returns.Total.ShouldBe(9m);
        day.Returns.Payments.ShouldBeEmpty();
        day.Returns.OnAccount.ShouldBe(9m);
    }

    [Fact]
    public void A_return_after_the_time_to_reduce_the_sales_gst_takes_back_the_value_and_not_the_gst()
    {
        var paid = Sale("NHPH260000002", 112m, 1, 12m, new SalePayment(PaymentMode.Upi, 112m, "UPI1"));
        var refund = paid.Items[0].Return(1).Value;
        var saleReturn = new PharmacySaleReturn
        {
            RefundAmount = refund,
            CashRefund = refund,
            RefundMode = PaymentMode.Cash,
            Items = [new PharmacySaleReturnItem { SaleItemId = paid.Items[0].Id, RefundAmount = refund, Quantity = 1 }],
        };
        var rates = new Dictionary<Guid, decimal> { [paid.Items[0].Id] = 12m };

        var late = PharmacyDayBook.Day(Today, [], [saleReturn], rates, [], timeBarredItems: new HashSet<Guid> { paid.Items[0].Id });
        var inTime = PharmacyDayBook.Day(Today, [], [saleReturn], rates, []);

        late.Returns.Tax.ShouldBe([new CounterTaxLine(12m, 112m, 0m, 0m)]);
        inTime.Returns.Tax.ShouldBe([new CounterTaxLine(12m, 100m, 6m, 6m)]);
        late.Returns.Total.ShouldBe(inTime.Returns.Total, "The money returned is the same; only the GST in it is not taken back.");
    }

    [Fact]
    public void A_return_paid_back_to_the_card_and_in_cash_counts_in_each_mode()
    {
        var split = Sale("NHPH260000004", 100m, 10, 12m, new SalePayment(PaymentMode.Card, 600m, "627910123456"), new SalePayment(PaymentMode.Cash, 400m, null));
        var refund = split.Items[0].Return(1).Value;
        var applied = split.ApplyReturn(refund, Now.AddHours(1), null).Value;
        var saleReturn = new PharmacySaleReturn
        {
            RefundAmount = refund,
            CashRefund = applied.PaidBack.Sum(p => p.Amount),
            RefundMode = PaymentMode.Card,
            Items = [new PharmacySaleReturnItem { SaleItemId = split.Items[0].Id, RefundAmount = refund, Quantity = 1 }],
        };

        var day = PharmacyDayBook.Day(Today, [], [saleReturn], new Dictionary<Guid, decimal> { [split.Items[0].Id] = 12m }, [],
            paidBack: [.. split.Payments.Where(p => p.IsRefund).Select(p => new PharmacyCollection(p.Mode, p.Amount))]);

        day.Returns.Payments.ShouldBe([new CounterMoney(CounterPaymentMode.Cash, 40m), new CounterMoney(CounterPaymentMode.Card, 60m)]);
        day.Returns.OnAccount.ShouldBe(0m);
    }

    [Fact]
    public void A_quiet_day_is_empty() =>
        PharmacyDayBook.Day(Today, [], [], new Dictionary<Guid, decimal>(), []).IsEmpty.ShouldBeTrue();

    /// <summary>
    /// The day of <see cref="Invoices_returns_and_later_collections_add_up_to_what_the_counter_took"/>: an invoice partly on
    /// credit whose balance is paid by card later, one paid by UPI and returned the same afternoon, and one cancelled.
    /// </summary>
    private static (PharmacySale Credit, PharmacySale Paid, PharmacySale Cancelled, PharmacySaleReturn Return) BusyDay()
    {
        var credit = Sale("NHPH260000001", 2.25m, 20, 5m, new(PaymentMode.Cash, 20m, null), new(PaymentMode.Credit, 25m, null));
        credit.CollectPayment(PaymentMode.Card, 25m, "627910123456", Now.AddHours(2), null).IsSuccess.ShouldBeTrue();
        var paid = Sale("NHPH260000002", 112m, 1, 12m, new SalePayment(PaymentMode.Upi, 112m, "UPI1"));
        var cancelled = Sale("NHPH260000003", 50m, 1, 12m, new SalePayment(PaymentMode.Cash, 50m, null));
        cancelled.Cancel("Wrong patient", Today, Today, Now.AddMinutes(5), null).IsSuccess.ShouldBeTrue();
        var refund = paid.Items[0].Return(1).Value;
        var applied = paid.ApplyReturn(refund, Now.AddHours(3), null).Value;
        var saleReturn = new PharmacySaleReturn
        {
            ReturnNumber = "NHPR260000001",
            SaleId = paid.Id,
            PatientId = paid.PatientId,
            ReturnedAt = Now.AddHours(3),
            RefundAmount = refund,
            AdjustedAgainstDue = applied.AdjustedAgainstDue,
            CashRefund = applied.PaidBack.Sum(p => p.Amount),
            RefundMode = PaymentMode.Upi,
            Items = [new PharmacySaleReturnItem { SaleItemId = paid.Items[0].Id, RefundAmount = refund, Quantity = 1 }],
        };
        return (credit, paid, cancelled, saleReturn);
    }

    [Fact]
    public void Invoice_by_invoice_each_invoice_return_and_balance_paid_comes_with_its_own_figures_and_references()
    {
        var (credit, paid, cancelled, saleReturn) = BusyDay();

        var day = PharmacyDayBook.Invoices(Today, [credit, paid, cancelled], [saleReturn], new[] { credit, paid, cancelled }.ToDictionary(s => s.Id));

        day.Counter.ShouldBe("pharmacy");
        day.Invoices.Select(i => i.Number).ShouldBe(["NHPH260000001", "NHPH260000002"], "A cancelled invoice never reaches the books.");
        day.Invoices[0].ShouldSatisfyAllConditions(
            i => i.PatientId.ShouldBe(credit.PatientId),
            i => i.Tax.ShouldBe([new CounterTaxLine(5m, 42.86m, 1.07m, 1.07m)]),
            i => i.Payments.ShouldBe([new CounterPayment(CounterPaymentMode.Cash, 20m)]),
            i => i.OnAccount.ShouldBe(25m),
            i => i.Total.ShouldBe(45m),
            i => i.Buyer.ShouldBeNull());
        day.Invoices[1].Payments.ShouldBe([new CounterPayment(CounterPaymentMode.Upi, 112m, "UPI1")]);

        var credited = day.Returns.ShouldHaveSingleItem();
        credited.ShouldSatisfyAllConditions(
            r => r.Number.ShouldBe("NHPR260000001"),
            r => r.InvoiceNumber.ShouldBe("NHPH260000002"),
            r => r.PatientId.ShouldBe(paid.PatientId),
            r => r.Tax.ShouldBe([new CounterTaxLine(12m, 100m, 6m, 6m)]),
            r => r.PaidBack.ShouldBe([new CounterPayment(CounterPaymentMode.Upi, 112m, "UPI1")]),
            r => r.AgainstDue.ShouldBe(0m));

        var collected = day.Collections.ShouldHaveSingleItem();
        collected.ShouldSatisfyAllConditions(
            c => c.InvoiceNumber.ShouldBe("NHPH260000001"),
            c => c.Payments.ShouldBe([new CounterPayment(CounterPaymentMode.Card, 25m, "627910123456")]));
    }

    [Fact]
    public void Invoice_by_invoice_the_day_adds_up_to_its_summary()
    {
        var (credit, paid, cancelled, saleReturn) = BusyDay();
        var paidBack = paid.Payments.Where(p => p.IsRefund).Select(p => new PharmacyCollection(p.Mode, p.Amount)).ToList();

        var summary = PharmacyDayBook.Day(Today, [credit, paid, cancelled], [saleReturn], new Dictionary<Guid, decimal> { [paid.Items[0].Id] = 12m },
            [new PharmacyCollection(PaymentMode.Card, 25m)], paidBack: paidBack);
        var invoices = PharmacyDayBook.Invoices(Today, [credit, paid, cancelled], [saleReturn], new[] { credit, paid, cancelled }.ToDictionary(s => s.Id));

        invoices.Invoices.Count.ShouldBe(summary.Sales.Count);
        invoices.Invoices.Sum(i => i.Total).ShouldBe(summary.Sales.Total);
        invoices.Invoices.Sum(i => i.OnAccount).ShouldBe(summary.Sales.OnAccount);
        invoices.Invoices.SelectMany(i => i.Payments).Sum(p => p.Amount).ShouldBe(summary.Sales.Payments.Sum(p => p.Amount));
        invoices.Returns.Count.ShouldBe(summary.Returns.Count);
        invoices.Returns.Sum(r => r.Total).ShouldBe(summary.Returns.Total);
        invoices.Returns.Sum(r => r.AgainstDue).ShouldBe(summary.Returns.OnAccount);
        invoices.Collections.Sum(c => c.Amount).ShouldBe(summary.Collected.Sum(c => c.Amount));
    }

    [Fact]
    public void A_b2b_invoice_names_its_buyer_and_place_of_supply_and_a_walk_in_customer_their_name()
    {
        var b2b = new PharmacySale { InvoiceNumber = "NHPH260000005", SoldAt = Now, CustomerName = " Kumily Clinic " };
        b2b.SetSupply("32AAACK1234F1Z9", "Kumily Clinic Pvt Ltd", "33", "32");
        var item = new PharmacySaleItem { ItemName = "Medicine", BatchNumber = "B1", UnitMrp = 112m, Quantity = 1, GstRate = 12m };
        item.Price(0, b2b.IsInterState);
        b2b.Items.Add(item);
        b2b.Complete([new SalePayment(PaymentMode.Credit, 112m, null)], creditAllowed: true, Now, null).IsSuccess.ShouldBeTrue();

        var invoice = PharmacyDayBook.Invoices(Today, [b2b], [], new Dictionary<Guid, PharmacySale> { [b2b.Id] = b2b }).Invoices.ShouldHaveSingleItem();

        invoice.ShouldSatisfyAllConditions(
            i => i.Buyer.ShouldBe(new CounterBuyer("32AAACK1234F1Z9", "Kumily Clinic Pvt Ltd", "32")),
            i => i.PatientId.ShouldBeNull(),
            i => i.CustomerName.ShouldBe("Kumily Clinic"),
            i => i.Tax.ShouldBe([new CounterTaxLine(12m, 100m, 0m, 0m, 12m)]),
            i => i.Payments.ShouldBeEmpty(),
            i => i.OnAccount.ShouldBe(112m));
    }

    [Fact]
    public void Invoice_by_invoice_a_return_after_the_time_to_reduce_the_sales_gst_takes_back_the_value_alone()
    {
        var soldAt = new DateTimeOffset(2025, 1, 10, 6, 30, 0, TimeSpan.Zero);
        var old = new PharmacySale { InvoiceNumber = "NHPH250000009", SoldAt = soldAt, PatientId = Guid.CreateVersion7() };
        var item = new PharmacySaleItem { ItemName = "Medicine", BatchNumber = "B1", UnitMrp = 112m, Quantity = 1, GstRate = 12m };
        item.Price(0);
        old.Items.Add(item);
        old.Complete([new SalePayment(PaymentMode.Cash, 112m, null)], creditAllowed: true, soldAt, null).IsSuccess.ShouldBeTrue();
        var refund = old.Items[0].Return(1).Value;
        var applied = old.ApplyReturn(refund, Now, null).Value;
        var saleReturn = new PharmacySaleReturn
        {
            ReturnNumber = "NHPR260000002",
            SaleId = old.Id,
            ReturnedAt = Now,
            RefundAmount = refund,
            CashRefund = applied.PaidBack.Sum(p => p.Amount),
            RefundMode = PaymentMode.Cash,
            Items = [new PharmacySaleReturnItem { SaleItemId = old.Items[0].Id, RefundAmount = refund, Quantity = 1 }],
        };

        var day = PharmacyDayBook.Invoices(Today, [], [saleReturn], new Dictionary<Guid, PharmacySale> { [old.Id] = old });

        day.Invoices.ShouldBeEmpty();
        day.Returns.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Tax.ShouldBe([new CounterTaxLine(12m, 112m, 0m, 0m)]),
            r => r.PaidBack.ShouldBe([new CounterPayment(CounterPaymentMode.Cash, 112m)]),
            r => r.InvoiceNumber.ShouldBe("NHPH250000009"));
        day.Collections.ShouldBeEmpty("The sale's own payment was taken when it was sold.");
    }

    [Fact]
    public void Invoice_by_invoice_a_quiet_day_is_empty() =>
        PharmacyDayBook.Invoices(Today, [], [], new Dictionary<Guid, PharmacySale>()).IsEmpty.ShouldBeTrue();
}
