using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A cancelled bill is reversed in full: Tally takes back its whole sale and GSTR-1 drops it. What was recorded against the
/// bill since (a credit note or write-off, a debit note, an insurer's settlement or deduction) stays where it is, so a
/// bill with any of them is not cancelled: it would be undone twice. A credit note takes back what remains, and what the
/// payer would not pay is recovered from the patient by a patient debit note.
/// </summary>
public sealed class BillCancellationTests
{
    [Fact]
    public void A_bill_with_a_credit_note_is_not_cancelled()
    {
        var bill = FinalizedBill(Charge(10_000m));
        bill.ApplyPayment(10_000m, allowExcess: false).IsSuccess.ShouldBeTrue();
        bill.ApplyCredit(2_000m).IsSuccess.ShouldBeTrue();
        bill.ApplyRefund(2_000m).IsSuccess.ShouldBeTrue();

        var refused = bill.Cancel("Rebill the patient", Guid.CreateVersion7(), Now);

        refused.Error!.Code.ShouldBe("billing.bill_adjusted");
        refused.Error.Message.ShouldContain("Issue a credit note for what remains");
        bill.Status.ShouldBe(BillStatus.Paid);
        bill.RefundableAmount.ShouldBe(0m, "the ₹8,000 the patient keeps paid for is not made refundable");
        bill.DomainEvents.OfType<BillCancelled>().ShouldBeEmpty();
    }

    [Fact]
    public void A_written_off_bill_is_not_cancelled()
    {
        var bill = FinalizedBill(Charge(1_000m));
        bill.ApplyPayment(400m, allowExcess: false).IsSuccess.ShouldBeTrue();

        // A write-off reduces what the patient owes as a credit note does.
        bill.ApplyCredit(600m).IsSuccess.ShouldBeTrue();

        bill.Cancel("Billed to the wrong patient", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe("billing.bill_adjusted");
        bill.Status.ShouldBe(BillStatus.Paid);
    }

    [Fact]
    public void A_bill_with_a_debit_note_is_not_cancelled()
    {
        var patient = FinalizedBill(Charge(1_000m));
        patient.ApplyDebit(250m, toPayer: false).IsSuccess.ShouldBeTrue();
        patient.Cancel("Billed to the wrong patient", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe("billing.bill_adjusted");

        var payer = InsuredBill(10_000m, payerCap: null);
        payer.ApplyDebit(500m, toPayer: true).IsSuccess.ShouldBeTrue();
        payer.Cancel("Billed to the wrong company", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe("billing.bill_adjusted");
    }

    [Fact]
    public void A_bill_the_insurer_settled_is_not_cancelled()
    {
        var bill = InsuredBill(60_000m, payerCap: 50_000m);
        bill.ApplyPayerSettlement(40_000m, 0m).IsSuccess.ShouldBeTrue();

        bill.Cancel("Rebill the patient", Guid.CreateVersion7(), Now).Error!.Code.ShouldBe("billing.bill_adjusted");
    }

    [Fact]
    public void A_bill_whose_claim_was_rejected_is_not_cancelled_and_a_patient_debit_note_recovers_the_share()
    {
        // The payer's ₹50,000 and the patient's ₹10,000, which the patient paid.
        var bill = InsuredBill(60_000m, payerCap: 50_000m);
        (bill.PayerAmount, bill.PatientAmount).ShouldBe((50_000m, 10_000m));
        bill.ApplyPayment(10_000m, allowExcess: false).IsSuccess.ShouldBeTrue();

        // The claim is rejected: its unpaid balance is disallowed on the bill (and journalled as a deduction in Tally).
        bill.ApplyPayerSettlement(0m, 50_000m).IsSuccess.ShouldBeTrue();

        var refused = bill.Cancel("Rebill the patient for the rejected claim", Guid.CreateVersion7(), Now);
        refused.Error!.Code.ShouldBe("billing.bill_adjusted");
        refused.Error.Message.ShouldContain("patient debit note");
        bill.Status.ShouldBe(BillStatus.Paid);
        bill.DomainEvents.OfType<BillCancelled>().ShouldBeEmpty();

        // The patient debit note charges the rejected share to the patient on the same bill.
        bill.ApplyDebit(50_000m, toPayer: false).IsSuccess.ShouldBeTrue();
        (bill.DueAmount, bill.Status, bill.PayerOutstanding).ShouldBe((50_000m, BillStatus.PartiallyPaid, 0m));
        bill.ApplyPayment(50_000m, allowExcess: false).IsSuccess.ShouldBeTrue();
        (bill.DueAmount, bill.Status).ShouldBe((0m, BillStatus.Paid));
    }

    [Fact]
    public void A_bill_with_nothing_recorded_against_it_still_cancels_even_with_a_payer()
    {
        // A claim rejected with nothing recorded against the bill leaves it as it was finalized.
        var bill = InsuredBill(60_000m, payerCap: 50_000m);
        bill.ApplyPayment(10_000m, allowExcess: false).IsSuccess.ShouldBeTrue();

        bill.Cancel("Billed to the wrong patient", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        (bill.Status, bill.RefundableAmount).ShouldBe((BillStatus.Cancelled, 10_000m));
        bill.DomainEvents.OfType<BillCancelled>().ShouldHaveSingleItem();
    }

    /// <summary>An insurer's bill of one payable service: the payer pays it all, up to <paramref name="payerCap"/>.</summary>
    private static Bill InsuredBill(decimal price, decimal? payerCap)
    {
        var terms = new BillPayerTerms(PayerType.Insurance, Guid.CreateVersion7(), null, Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, payerCap);
        var bill = DraftBill(terms, Charge(price));
        bill.Finalize("NHIB260000001", null, Now).IsSuccess.ShouldBeTrue();
        return bill;
    }
}
