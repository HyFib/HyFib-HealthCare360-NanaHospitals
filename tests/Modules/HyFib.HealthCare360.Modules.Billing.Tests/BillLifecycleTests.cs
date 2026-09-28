using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class BillLifecycleTests
{
    [Fact]
    public void Finalizing_freezes_the_bill_under_its_number_and_notifies_other_modules()
    {
        var bill = DraftBill(null, Charge(500m), Charge(250.50m));

        bill.Finalize("NHOB260000042", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        bill.Status.ShouldBe(BillStatus.Finalized);
        bill.BillNumber.ShouldBe("NHOB260000042");
        bill.NetAmount.ShouldBe(751m);
        bill.DueAmount.ShouldBe(751m);
        var finalized = bill.DomainEvents.OfType<BillFinalized>().Single();
        finalized.NetAmount.ShouldBe(751m);
    }

    [Fact]
    public void An_empty_bill_cannot_be_finalized()
    {
        DraftBill(null).Finalize("X", null, Now).Error!.Code.ShouldBe("billing.bill_empty");
    }

    [Fact]
    public void A_finalized_bill_cannot_change()
    {
        var bill = FinalizedBill(Charge(100m));

        bill.AddCharge(Charge(50m), true, null).Error!.Code.ShouldBe("billing.bill_not_draft");
        bill.SetUserDiscounts(10m, null, new Dictionary<Guid, decimal>(), "x").IsFailure.ShouldBeTrue();
        bill.Finalize("Y", null, Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Another_patients_service_cannot_be_added()
    {
        var bill = DraftBill(null);

        bill.AddCharge(Charge(100m, patientId: Guid.CreateVersion7()), true, null).Error!.Code.ShouldBe("billing.charge_other_patient");
    }

    [Fact]
    public void Removing_a_draft_line_returns_its_charge()
    {
        var charge = Charge(100m);
        var bill = DraftBill(null, charge, Charge(40m));

        var removed = bill.RemoveLine(bill.Lines.Single(l => l.ChargeId == charge.Id).Id);
        bill.Recalculate();

        removed.Value.ShouldBe(charge.Id);
        bill.NetAmount.ShouldBe(40m);
    }

    [Fact]
    public void Staff_discounts_are_applied_on_recalculation()
    {
        var bill = DraftBill(null, Charge(1_000m));

        bill.SetUserDiscounts(null, 150m, new Dictionary<Guid, decimal>(), "Senior citizen").IsSuccess.ShouldBeTrue();
        bill.Recalculate();

        bill.BillDiscountAmount.ShouldBe(150m);
        bill.NetAmount.ShouldBe(850m);
        bill.DiscountReason.ShouldBe("Senior citizen");
    }

    [Fact]
    public void Payments_move_the_bill_from_finalized_to_partially_paid_to_paid()
    {
        var bill = FinalizedBill(Charge(1_000m));

        bill.ApplyPayment(400m, allowExcess: false).IsSuccess.ShouldBeTrue();
        bill.Status.ShouldBe(BillStatus.PartiallyPaid);
        bill.DueAmount.ShouldBe(600m);

        bill.ApplyPayment(600m, allowExcess: false).IsSuccess.ShouldBeTrue();
        bill.Status.ShouldBe(BillStatus.Paid);
        bill.DueAmount.ShouldBe(0m);
    }

    [Fact]
    public void Settling_a_bill_in_full_is_announced_once_whatever_paid_it()
    {
        var bill = FinalizedBill(Charge(1_000m));
        bill.DomainEvents.OfType<BillPaid>().ShouldBeEmpty();

        bill.ApplyPayment(400m, allowExcess: false);
        bill.ApplyPayment(600m, allowExcess: false);
        bill.ApplyPayment(50m, allowExcess: true).IsSuccess.ShouldBeTrue();

        bill.DomainEvents.OfType<BillPaid>().ShouldHaveSingleItem().BillId.ShouldBe(bill.Id);
    }

    [Fact]
    public void A_credit_note_clearing_the_due_is_not_a_payment()
    {
        var bill = FinalizedBill(Charge(1_000m));

        bill.ApplyCredit(1_000m).IsSuccess.ShouldBeTrue();

        bill.Status.ShouldBe(BillStatus.Paid);
        bill.DomainEvents.OfType<BillPaid>().ShouldBeEmpty();
    }

    [Fact]
    public void A_bill_the_payer_covers_in_full_is_paid_the_moment_it_is_finalized()
    {
        var terms = new BillPayerTerms(PayerType.Corporate, Guid.CreateVersion7(), null, null, null, 0m, null);
        var bill = DraftBill(terms, Charge(2_500m));

        bill.Finalize("NHOB260000043", null, Now).IsSuccess.ShouldBeTrue();

        bill.PatientAmount.ShouldBe(0m);
        bill.Status.ShouldBe(BillStatus.Paid);
        bill.DomainEvents.OfType<BillPaid>().ShouldHaveSingleItem();
    }

    [Fact]
    public void The_counter_cannot_take_more_than_the_balance()
    {
        var bill = FinalizedBill(Charge(1_000m));

        bill.ApplyPayment(1_000.01m, allowExcess: false).Error!.Code.ShouldBe("billing.overpayment");
        bill.PaidAmount.ShouldBe(0m);
    }

    [Fact]
    public void Money_already_captured_online_is_recorded_and_any_excess_becomes_refundable()
    {
        var bill = FinalizedBill(Charge(1_000m));
        bill.ApplyPayment(1_000m, allowExcess: false);

        bill.ApplyPayment(1_000m, allowExcess: true).IsSuccess.ShouldBeTrue();

        bill.Status.ShouldBe(BillStatus.Paid);
        bill.RefundableAmount.ShouldBe(1_000m);
    }

    [Fact]
    public void Draft_bills_cannot_take_payments()
    {
        DraftBill(null, Charge(100m)).ApplyPayment(100m, allowExcess: true).Error!.Code.ShouldBe("billing.bill_not_finalized");
    }

    [Fact]
    public void A_credit_note_reduces_the_due_and_is_limited_to_the_patient_share()
    {
        var bill = FinalizedBill(Charge(1_000m));

        bill.ApplyCredit(200m).IsSuccess.ShouldBeTrue();
        bill.DueAmount.ShouldBe(800m);
        bill.ApplyCredit(800.01m).Error!.Code.ShouldBe("billing.credit_exceeds");
    }

    [Fact]
    public void A_credit_note_on_a_paid_bill_makes_the_excess_refundable()
    {
        var bill = FinalizedBill(Charge(1_000m));
        bill.ApplyPayment(1_000m, allowExcess: false);

        bill.ApplyCredit(300m);

        bill.RefundableAmount.ShouldBe(300m);
        bill.ApplyRefund(300.01m).Error!.Code.ShouldBe("billing.refund_exceeds");
        bill.ApplyRefund(300m).IsSuccess.ShouldBeTrue();
        bill.RefundableAmount.ShouldBe(0m);
    }

    [Fact]
    public void Cancelling_a_paid_bill_makes_the_payment_refundable_and_a_full_refund_marks_it_refunded()
    {
        var bill = FinalizedBill(Charge(1_000m));
        bill.ApplyPayment(1_000m, allowExcess: false);

        bill.Cancel("Billed to the wrong patient", Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();

        bill.Status.ShouldBe(BillStatus.Cancelled);
        bill.DueAmount.ShouldBe(0m);
        bill.RefundableAmount.ShouldBe(1_000m);
        bill.DomainEvents.OfType<BillCancelled>().ShouldHaveSingleItem();

        bill.ApplyRefund(1_000m).IsSuccess.ShouldBeTrue();
        bill.Status.ShouldBe(BillStatus.Refunded);
    }

    [Fact]
    public void Drafts_are_discarded_not_cancelled_and_a_bill_is_cancelled_only_once()
    {
        DraftBill(null, Charge(100m)).Cancel("x", null, Now).Error!.Code.ShouldBe("billing.discard_draft");

        var bill = FinalizedBill(Charge(100m));
        bill.Cancel("x", null, Now).IsSuccess.ShouldBeTrue();
        bill.Cancel("x", null, Now).Error!.Code.ShouldBe("billing.bill_cancelled");
    }

    [Fact]
    public void A_bill_fully_covered_by_the_payer_is_paid_from_the_patients_point_of_view()
    {
        var terms = new BillPayerTerms(PayerType.Corporate, Guid.CreateVersion7(), null, null, null, 0m, null);
        var bill = DraftBill(terms, Charge(5_000m));

        bill.Finalize("NHOB260000050", null, Now);

        bill.PayerAmount.ShouldBe(5_000m);
        bill.PatientAmount.ShouldBe(0m);
        bill.Status.ShouldBe(BillStatus.Paid);
        bill.PayerOutstanding.ShouldBe(5_000m);
    }

    [Fact]
    public void Payer_settlements_cannot_exceed_what_the_payer_owes()
    {
        var terms = new BillPayerTerms(PayerType.Insurance, Guid.CreateVersion7(), null, null, null, 0m, 10_000m);
        var bill = DraftBill(terms, Charge(10_000m));
        bill.Finalize("NHIB260000001", null, Now);

        bill.ApplyPayerSettlement(9_000m, 1_000.01m).Error!.Code.ShouldBe("billing.payer_settlement_exceeds");
        bill.ApplyPayerSettlement(9_000m, 1_000m).IsSuccess.ShouldBeTrue();
        bill.PayerOutstanding.ShouldBe(0m);
    }

    [Fact]
    public void Package_cover_is_clamped_to_the_line_value()
    {
        var bill = DraftBill(null, Charge(1_000m));
        var line = bill.Lines.Single();

        bill.ApplyPackageCoverage(new Dictionary<Guid, decimal> { [line.Id] = 5_000m });
        bill.Recalculate();

        line.PackageCoveredAmount.ShouldBe(1_000m);
        bill.NetAmount.ShouldBe(0m);
    }
}
