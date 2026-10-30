using HyFib.HealthCare360.Modules.Billing.Domain;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A bill free of cost by law: the victim of a sexual offence or an acid attack is treated free (BNSS 2023, s.397). Every
/// line comes to nothing, for the patient and for any payer. What the services were worth is kept as the bill's free
/// treatment, and there is no tax, no staff discount and nothing to collect.
/// </summary>
public sealed class FreeTreatmentBillTests
{
    private const string Reason = "BNSS 2023 s.397, MLC no. NHMLC2600012";

    [Fact]
    public void A_free_bill_charges_nothing_to_anyone_and_keeps_what_each_service_was_worth()
    {
        // A room over ₹5,000 a day carries GST; a test at a 10% tariff concession; a line a package absorbs in part; a
        // return. An insurer with a co-pay, and a 10% bill discount asked for.
        BillLineInput[] lines = [Line(6_000m, taxRate: 5m), Line(1_000m, tariffPercent: 10m), Line(2_000m, covered: 500m), Line(-200m)];
        var insured = new SplitTerms(true, 10m, null);
        BillTotalsCalculator.Calculate(lines, new BillDiscountInput(10m, null), insured).Net.ShouldBeGreaterThan(0m);

        var totals = BillTotalsCalculator.Calculate(lines, new BillDiscountInput(10m, null), insured, freeTreatment: true);

        totals.Lines.Select(l => l.FreeTreatment).ShouldBe([6_000m, 900m, 1_500m, -200m]);
        totals.FreeTreatment.ShouldBe(8_200m);
        (totals.Gross, totals.PackageCovered, totals.TariffDiscount).ShouldBe((8_800m, 500m, 100m), "Package cover and concessions say what a line was worth.");
        (totals.Net, totals.PatientAmount, totals.PayerAmount, totals.CoPay, totals.NonPayable, totals.RoundOff).ShouldBe((0m, 0m, 0m, 0m, 0m, 0m));
        (totals.Taxable, totals.Tax, totals.Cgst, totals.Sgst).ShouldBe((0m, 0m, 0m, 0m), "Nothing is charged, so there is no taxable value.");
        (totals.LineDiscount, totals.BillDiscount, totals.UserDiscount).ShouldBe((0m, 0m, 0m), "Free treatment is no staff discount, and needs no approval.");
        totals.Lines.ShouldAllBe(l => l.Net == 0m && l.Taxable == 0m && l.Tax == 0m && l.LineDiscount == 0m && l.BillDiscountShare == 0m);
    }

    [Fact]
    public void A_draft_of_a_visit_treated_free_comes_to_nothing_and_is_settled_without_payment()
    {
        var bill = DraftBill(null, Charge(1_000m), Charge(500m, taxRate: 18m));
        bill.SetUserDiscounts(null, 150m, new Dictionary<Guid, decimal> { [bill.Lines[0].Id] = 100m }, "Senior citizen").IsSuccess.ShouldBeTrue();
        bill.Recalculate();
        (bill.BillDiscountAmount, bill.LineDiscountAmount, bill.TaxAmount > 0m).ShouldBe((150m, 100m, true));

        bill.SetFreeTreatment(Reason);
        bill.Recalculate();

        (bill.NetAmount, bill.PatientAmount, bill.DueAmount, bill.TaxAmount).ShouldBe((0m, 0m, 0m, 0m));
        bill.FreeTreatmentAmount.ShouldBe(1_500m);
        bill.Lines.ShouldAllBe(l => l.NetAmount == 0m && l.LineDiscountAmount == 0m);
        (bill.BillDiscountValue, bill.DiscountReason, bill.LineDiscountAmount, bill.BillDiscountAmount).ShouldBe((null, null, 0m, 0m),
            "Its staff discounts are cleared: nothing is left to discount.");

        // No discount may be given on it. Finalized, it is settled with nothing paid, and nothing can be collected.
        bill.SetUserDiscounts(10m, null, new Dictionary<Guid, decimal>(), "Staff").Error!.Code.ShouldBe("billing.free_treatment");
        bill.Finalize("NHOB260000002", null, Now).IsSuccess.ShouldBeTrue();
        (bill.Status, bill.FreeTreatmentReason, bill.FreeTreatmentAmount).ShouldBe((BillStatus.Paid, Reason, 1_500m));
        bill.ApplyPayment(100m, allowExcess: false).Error!.Code.ShouldBe("billing.bill_not_payable");
    }

    [Fact]
    public void Only_a_draft_is_made_free_or_charged_again()
    {
        var finalized = FinalizedBill(Charge(1_000m));
        finalized.SetFreeTreatment(Reason);
        (finalized.FreeTreatmentReason, finalized.NetAmount).ShouldBe((null, 1_000m), "A finalized bill stays as it was issued.");

        var draft = DraftBill(null, Charge(1_000m));
        draft.SetFreeTreatment(Reason);
        draft.Recalculate();
        draft.NetAmount.ShouldBe(0m);
        draft.SetFreeTreatment(null);
        draft.Recalculate();
        (draft.NetAmount, draft.FreeTreatmentAmount, draft.FreeTreatmentReason).ShouldBe((1_000m, 0m, null),
            "A cause corrected before the bill is final bills it as usual again.");
    }
}
