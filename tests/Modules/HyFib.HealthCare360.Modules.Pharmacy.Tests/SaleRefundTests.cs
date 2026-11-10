namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>
/// Money paid back at the pharmacy counter goes back the way it came (maker-checker, slice 2): a card sale to the card, a
/// UPI sale by UPI, never cash from the till; a sale paid partly by card and partly in cash in proportion; no one pays back
/// a sale they made or took money on; and a return not put back on the shelf is a write-off that waits for approval even
/// when no money goes out.
/// </summary>
public sealed class SaleRefundTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 5, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 7);
    private static readonly Guid Seller = Guid.CreateVersion7();

    private static PharmacySale Sale(decimal mrp, decimal quantity, DateOnly? expiry, params SalePayment[] payments)
    {
        var sale = new PharmacySale { InvoiceNumber = "NHPH260000101", SoldAt = Now, PatientId = Guid.CreateVersion7(), SoldByUserId = Seller };
        var item = new PharmacySaleItem { ItemName = "Amoxicillin 500 mg", BatchNumber = "AX1", UnitMrp = mrp, Quantity = quantity, GstRate = 12, ExpiryDate = expiry };
        item.Price(0);
        sale.Items.Add(item);
        sale.Complete(payments, creditAllowed: true, Now, Seller).IsSuccess.ShouldBeTrue();
        return sale;
    }

    [Theory]
    [InlineData(PaymentMode.Card, "627910123456")]
    [InlineData(PaymentMode.Upi, "AXISN41234567890")]
    public void A_card_or_UPI_sale_cancelled_is_paid_back_to_the_card_or_by_UPI_never_in_cash(PaymentMode mode, string reference)
    {
        var sale = Sale(45m, 1, null, new SalePayment(mode, 45m, reference));
        var taken = sale.Payments.Single();

        var paidBack = sale.Cancel("Billed to the wrong customer", Today, Today, Now.AddMinutes(10), Guid.CreateVersion7()).Value;

        // Under the sale's own reference, as the acquirer reports the refund, so the card and UPI settlement matches it.
        paidBack.ShouldBe([new RefundPart(taken.Id, mode, 45m, reference)]);
        var refund = sale.Payments.Single(p => p.IsRefund);
        refund.ShouldSatisfyAllConditions(
            r => r.Mode.ShouldBe(mode),
            r => r.Amount.ShouldBe(45m),
            r => r.Reference.ShouldBe(reference),
            r => r.RefundOfPaymentId.ShouldBe(taken.Id));
        sale.Payments.ShouldNotContain(p => p.IsRefund && p.Mode == PaymentMode.Cash, "no cash leaves the till for money that came in by card or UPI");
        sale.RefundedAmount.ShouldBe(45m);
    }

    [Fact]
    public void A_return_from_a_sale_paid_by_card_and_in_cash_goes_back_in_proportion_and_never_more_than_each_brought()
    {
        var sale = Sale(100m, 10, null, new SalePayment(PaymentMode.Card, 600m, "627910123456"), new SalePayment(PaymentMode.Cash, 400m, null));
        var card = sale.Payments.Single(p => p.Mode == PaymentMode.Card);
        var cash = sale.Payments.Single(p => p.Mode == PaymentMode.Cash);

        // One strip of ten: ₹100, ₹40 in cash and ₹60 to the card.
        sale.ApplyReturn(sale.Items[0].Return(1).Value, Now.AddHours(1), null).Value.PaidBack
            .ShouldBe([new RefundPart(cash.Id, PaymentMode.Cash, 40m, null), new RefundPart(card.Id, PaymentMode.Card, 60m, "627910123456")]);

        // A share that does not come to whole paise: rounded down, the paisa left over to the larger remainder.
        SaleRefunds.Plan(sale.Payments, 33.33m).ShouldBe([new RefundPart(cash.Id, PaymentMode.Cash, 13.33m, null), new RefundPart(card.Id, PaymentMode.Card, 20.00m, "627910123456")]);

        // The other nine strips: what is left of each payment, so the card gets back exactly what it paid and no more.
        sale.ApplyReturn(sale.Items[0].Return(9).Value, Now.AddHours(2), null).Value.PaidBack
            .ShouldBe([new RefundPart(cash.Id, PaymentMode.Cash, 360m, null), new RefundPart(card.Id, PaymentMode.Card, 540m, "627910123456")]);
        sale.Payments.Where(p => p.IsRefund && p.Mode == PaymentMode.Card).Sum(p => p.Amount).ShouldBe(600m);
        sale.Payments.Where(p => p.IsRefund && p.Mode == PaymentMode.Cash).Sum(p => p.Amount).ShouldBe(400m);
        SaleRefunds.Plan(sale.Payments, 1m).ShouldBeEmpty("nothing is left to pay back");
    }

    [Fact]
    public void A_balance_due_is_set_off_first_and_a_later_card_collection_is_paid_back_to_the_card()
    {
        var sale = Sale(100m, 10, null, new SalePayment(PaymentMode.Cash, 200m, null), new SalePayment(PaymentMode.Credit, 800m, null));
        sale.CollectPayment(PaymentMode.Card, 300m, "SLIP9", Now.AddHours(1), Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();

        // ₹700 returned: ₹500 still due comes off first; ₹200 goes back across the cash (₹200) and the card collection (₹300).
        var plan = sale.PlanReturn([new ReturnInput(sale.Items[0].Id, 7, true)], Today).Value;

        plan.ShouldSatisfyAllConditions(
            p => p.Refund.ShouldBe(700m),
            p => p.AgainstDue.ShouldBe(500m),
            p => p.PaidBack.Select(r => (r.Mode, r.Amount)).ShouldBe([(PaymentMode.Cash, 80m), (PaymentMode.Card, 120m)]),
            p => p.NeedsApproval.ShouldBeTrue("money goes out"));
    }

    [Fact]
    public void No_one_cancels_or_pays_back_a_sale_they_made_or_took_money_on()
    {
        var collector = Guid.CreateVersion7();
        var sale = Sale(100m, 2, null, new SalePayment(PaymentMode.Cash, 50m, null), new SalePayment(PaymentMode.Credit, 150m, null));
        sale.CollectPayment(PaymentMode.Upi, 150m, "UPI7", Now.AddHours(1), collector).IsSuccess.ShouldBeTrue();

        sale.HandledBy(Seller).ShouldBeTrue("the pharmacist who sold it");
        sale.HandledBy(collector).ShouldBeTrue("the one who collected its balance");
        sale.HandledBy(Guid.CreateVersion7()).ShouldBeFalse();
        sale.HandledBy(null).ShouldBeFalse();
    }

    [Fact]
    public void A_return_not_restocked_is_a_write_off_that_waits_for_approval_even_when_nothing_is_paid_back()
    {
        // A sale on the patient's account: a return comes off the balance due, and no money goes out.
        var sale = Sale(50m, 4, Today.AddMonths(6), new SalePayment(PaymentMode.Credit, 200m, null));

        var restocked = sale.PlanReturn([new ReturnInput(sale.Items[0].Id, 1, true)], Today).Value;
        restocked.ShouldSatisfyAllConditions(
            p => p.PaidBack.ShouldBeEmpty(),
            p => p.WritesOff.ShouldBeFalse(),
            p => p.NeedsApproval.ShouldBeFalse("back on the shelf and set against the due: made at once"));

        var damaged = sale.PlanReturn([new ReturnInput(sale.Items[0].Id, 1, false)], Today).Value;
        damaged.ShouldSatisfyAllConditions(
            p => p.PaidBack.ShouldBeEmpty(),
            p => p.AgainstDue.ShouldBe(50m),
            p => p.WritesOff.ShouldBeTrue(),
            p => p.NeedsApproval.ShouldBeTrue("stock written off waits for a second person"));

        // Ticked to go back on the shelf, an expired strip is still not restocked: a write-off.
        var expired = Sale(50m, 4, Today.AddDays(-1), new SalePayment(PaymentMode.Credit, 200m, null));
        var plan = expired.PlanReturn([new ReturnInput(expired.Items[0].Id, 1, true)], Today).Value;
        plan.Lines.Single().ShouldSatisfyAllConditions(l => l.Restock.ShouldBeFalse(), l => l.Expired.ShouldBeTrue());
        plan.NeedsApproval.ShouldBeTrue();

        // Working it out changes nothing on the invoice.
        sale.Items[0].ReturnedQuantity.ShouldBe(0);
        sale.DueAmount.ShouldBe(200m);
    }

    [Fact]
    public void A_ward_return_not_restocked_is_a_write_off_and_one_restocked_is_not()
    {
        var issue = new IpIssue { Number = "NHIPI26000009", PatientId = Guid.CreateVersion7(), AdmissionId = Guid.CreateVersion7(), IssuedAt = Now };
        var morphine = new IpIssueLine
        {
            ItemName = "Morphine 10 mg/ml injection",
            BatchNumber = "M1",
            Quantity = 4,
            UnitMrp = 25m,
            UnitPrice = 25m,
            IsControlled = true,
            ExpiryDate = Today.AddMonths(8),
        };
        issue.Lines.Add(morphine);

        issue.PlanReturn([new ReturnInput(morphine.Id, 1, true)], Today).Value.WritesOff.ShouldBeFalse();
        var opened = issue.PlanReturn([new ReturnInput(morphine.Id, 1, false)], Today).Value;
        opened.ShouldSatisfyAllConditions(p => p.WritesOff.ShouldBeTrue(), p => p.Credit.ShouldBe(25m));
        issue.PlanReturn([new ReturnInput(morphine.Id, 5, false)], Today).Error!.Code.ShouldBe("ward_return.exceeds_issued");
        morphine.ReturnedQuantity.ShouldBe(0);
    }
}
