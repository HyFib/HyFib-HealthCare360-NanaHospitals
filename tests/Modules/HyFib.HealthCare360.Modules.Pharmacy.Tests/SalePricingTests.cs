namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>GST at MRP, invoice rounding, payment splits and refunds at the pharmacy counter.</summary>
public sealed class SalePricingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 25);

    [Fact]
    public void Mrp_line_carves_gst_out_of_the_price_and_splits_cgst_sgst()
    {
        var line = SalePricing.PriceLine(unitMrp: 3.5m, quantity: 10, gstRate: 5, discountPercent: 0);

        line.ShouldBe(new SaleLinePrice(35m, 0m, 33.33m, 0.84m, 0.83m, 35m));
    }

    [Fact]
    public void Discount_reduces_the_inclusive_value_before_tax_is_carved_out()
    {
        var line = SalePricing.PriceLine(3.5m, 10, 5, 10);

        line.ShouldBe(new SaleLinePrice(35m, 3.50m, 30m, 0.75m, 0.75m, 31.50m));
    }

    [Fact]
    public void Invoice_total_is_rounded_to_the_rupee_with_a_round_off()
    {
        var totals = SalePricing.Total([SalePricing.PriceLine(3.5m, 10, 5, 0), SalePricing.PriceLine(3.5m, 10, 5, 10)]);

        totals.Net.ShouldBe(67m);
        totals.RoundOff.ShouldBe(0.50m);
        totals.Taxable.ShouldBe(63.33m);
        (totals.Cgst + totals.Sgst).ShouldBe(3.17m);
    }

    [Fact]
    public void Split_payments_must_add_up_exactly_and_set_the_status()
    {
        SalePricing.Settle(67m, [new(PaymentMode.Cash, 50m, null), new(PaymentMode.Upi, 17m, "UPI123")], creditAllowed: false).Value
            .ShouldBe(new Settlement(67m, 0m, SaleStatus.Paid));
        SalePricing.Settle(67m, [new(PaymentMode.Cash, 20m, null), new(PaymentMode.Credit, 47m, null)], creditAllowed: true).Value
            .ShouldBe(new Settlement(20m, 47m, SaleStatus.PartiallyPaid));
        SalePricing.Settle(67m, [new(PaymentMode.Credit, 67m, null)], creditAllowed: true).Value.Status.ShouldBe(SaleStatus.Credit);
        SalePricing.Settle(67m, [new(PaymentMode.Cash, 60m, null)], creditAllowed: false).Error!.Code.ShouldBe("payment.mismatch");
        SalePricing.Settle(67m, [], creditAllowed: false).IsFailure.ShouldBeTrue();
        SalePricing.Settle(0m, [], creditAllowed: false).Value.Status.ShouldBe(SaleStatus.Paid);
    }

    [Fact]
    public void Walk_in_customers_cannot_buy_on_credit()
    {
        SalePricing.Settle(67m, [new(PaymentMode.Credit, 67m, null)], creditAllowed: false).Error!.Code.ShouldBe("payment.credit_not_allowed");
    }

    [Fact]
    public void Partial_returns_never_drift_from_what_was_paid()
    {
        var first = SalePricing.RefundFor(10m, 3, 0, 0, 1);
        var second = SalePricing.RefundFor(10m, 3, 1, first, 1);
        var last = SalePricing.RefundFor(10m, 3, 2, first + second, 1);

        (first, second, last).ShouldBe((3.33m, 3.33m, 3.34m));
        (first + second + last).ShouldBe(10m);
    }

    private static PharmacySale Sale(params (decimal Mrp, decimal Quantity)[] lines)
    {
        var sale = new PharmacySale { InvoiceNumber = "NHPH260000001", SoldAt = Now, PatientId = Guid.CreateVersion7() };
        foreach (var (mrp, quantity) in lines)
        {
            var item = new PharmacySaleItem { ItemName = "Paracetamol 650 mg", BatchNumber = "B1", UnitMrp = mrp, Quantity = quantity, GstRate = 5 };
            item.Price(0);
            sale.Items.Add(item);
        }

        return sale;
    }

    [Fact]
    public void Credit_balances_are_collected_later_but_never_over_collected()
    {
        var sale = Sale((2.25m, 20));
        sale.Complete([new(PaymentMode.Cash, 20m, null), new(PaymentMode.Credit, 25m, null)], creditAllowed: true, Now, null).IsSuccess.ShouldBeTrue();
        sale.DueAmount.ShouldBe(25m);

        sale.CollectPayment(PaymentMode.Card, 30m, "SLIP9", Now, null).Error!.Code.ShouldBe("payment.exceeds_due");
        sale.CollectPayment(PaymentMode.Card, 25m, "SLIP9", Now, null).IsSuccess.ShouldBeTrue();

        sale.Status.ShouldBe(SaleStatus.Paid);
        sale.PaidAmount.ShouldBe(45m);
        sale.Payments.Count.ShouldBe(3);
    }

    [Fact]
    public void Returns_reduce_dues_before_cash_is_paid_back()
    {
        var sale = Sale((2.25m, 20));
        sale.Complete([new(PaymentMode.Cash, 35m, null), new(PaymentMode.Credit, 10m, null)], creditAllowed: true, Now, null);

        var refund = sale.Items[0].Return(10).Value;
        var (adjusted, cash) = sale.ApplyReturn(refund, PaymentMode.Cash, null, Now, null).Value;

        refund.ShouldBe(22.50m);
        adjusted.ShouldBe(10m);
        cash.ShouldBe(12.50m);
        sale.DueAmount.ShouldBe(0m);
        sale.Items[0].Return(11).Error!.Code.ShouldBe("return.exceeds_sold");
    }

    [Fact]
    public void Invoices_can_be_cancelled_only_on_the_same_day_and_before_returns()
    {
        var old = Sale((2.25m, 20));
        old.Complete([new(PaymentMode.Cash, 45m, null)], false, Now, null);
        old.Cancel("wrong patient", Today, Today.AddDays(-1), Now, null).Error!.Code.ShouldBe("sale.cancel_window");

        var returned = Sale((2.25m, 20));
        returned.Complete([new(PaymentMode.Cash, 45m, null)], false, Now, null);
        returned.ApplyReturn(returned.Items[0].Return(2).Value, PaymentMode.Cash, null, Now, null);
        returned.Cancel("wrong patient", Today, Today, Now, null).Error!.Code.ShouldBe("sale.has_returns");

        var sale = Sale((2.25m, 20));
        sale.Complete([new(PaymentMode.Upi, 45m, "UPI1")], false, Now, null);
        sale.Cancel("wrong patient", Today, Today, Now, null).IsSuccess.ShouldBeTrue();
        sale.Status.ShouldBe(SaleStatus.Cancelled);
        sale.RefundedAmount.ShouldBe(45m);
        sale.Payments.Count(p => p.IsRefund).ShouldBe(1);
    }

    [Fact]
    public void Dispensing_to_a_registered_patient_feeds_the_medication_record()
    {
        var sale = Sale((2.25m, 20));

        sale.MarkDispensed(null);

        var dispensed = sale.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<MedicationDispensed>();
        dispensed.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(20);
    }
}
