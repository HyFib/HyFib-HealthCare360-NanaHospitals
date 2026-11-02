using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// Money recorded as moved that did not move (audit P6): what a receipt reversal and a refund not paid out do to a bill,
/// an advance and an IP deposit, and which receipts and refunds may be reversed at all.
/// </summary>
public sealed class ReversalTests
{
    [Fact]
    public void A_paid_bill_whose_money_did_not_arrive_owes_it_again_and_says_it_is_no_longer_paid()
    {
        var bill = PaidBill(600m);

        bill.ReversePayment(600m).IsSuccess.ShouldBeTrue();

        (bill.Status, bill.PaidAmount, bill.DueAmount, bill.RefundableAmount).ShouldBe((BillStatus.Finalized, 0m, 600m, 0m));
        bill.DomainEvents.OfType<BillPaymentReversed>().ShouldHaveSingleItem().BillId.ShouldBe(bill.Id);
    }

    [Fact]
    public void Part_of_the_money_reversed_leaves_the_bill_part_paid_and_a_bill_not_paid_says_nothing()
    {
        var paid = PaidBill(600m);
        paid.ReversePayment(200m).IsSuccess.ShouldBeTrue();
        (paid.Status, paid.DueAmount).ShouldBe((BillStatus.PartiallyPaid, 200m));
        paid.DomainEvents.OfType<BillPaymentReversed>().ShouldHaveSingleItem();

        var part = FinalizedBill(Charge(600m));
        part.ApplyPayment(400m, allowExcess: false);
        part.ClearDomainEvents();
        part.ReversePayment(100m).IsSuccess.ShouldBeTrue();
        (part.Status, part.DueAmount).ShouldBe((BillStatus.PartiallyPaid, 300m));
        part.DomainEvents.OfType<BillPaymentReversed>().ShouldBeEmpty("it was not paid in full before");
    }

    [Fact]
    public void Money_refunded_from_a_bill_is_not_reversed_until_the_refund_is_undone()
    {
        var bill = PaidBill(600m);
        bill.Cancel("Billed to the wrong patient", Guid.CreateVersion7(), Now);
        bill.ApplyRefund(600m);
        bill.Status.ShouldBe(BillStatus.Refunded);

        bill.CanReversePayment(600m).Error!.Code.ShouldBe("billing.reversal_exceeds_held");
        bill.ReversePayment(1m).Error!.Code.ShouldBe("billing.reversal_exceeds_held");
        bill.PaidAmount.ShouldBe(600m);

        bill.ReverseRefund(600m).IsSuccess.ShouldBeTrue();
        (bill.Status, bill.RefundableAmount).ShouldBe((BillStatus.Cancelled, 600m));
        bill.ReverseRefund(1m).Error!.Code.ShouldBe("billing.refund_reversal_exceeds");

        bill.BalancesAfterReversal(600m).ShouldBe((0m, 0m));
        bill.ReversePayment(600m).IsSuccess.ShouldBeTrue();
        (bill.Status, bill.PaidAmount, bill.DueAmount, bill.RefundableAmount).ShouldBe((BillStatus.Cancelled, 0m, 0m, 0m));
    }

    [Fact]
    public void The_balances_shown_before_a_reversal_are_the_ones_it_leaves()
    {
        var overpaid = FinalizedBill(Charge(600m));
        overpaid.ApplyPayment(750m, allowExcess: true);
        overpaid.RefundableAmount.ShouldBe(150m);

        var preview = overpaid.BalancesAfterReversal(750m);
        overpaid.ReversePayment(750m);

        preview.ShouldBe((overpaid.DueAmount, overpaid.RefundableAmount));
        preview.ShouldBe((600m, 0m));
        overpaid.ReversePayment(0m).Error!.Code.ShouldBe("billing.amount_invalid");
    }

    [Fact]
    public void An_advance_gives_up_only_what_it_still_holds()
    {
        var advance = new PatientAdvance { ReceiptNumber = "NHRC260000123", Amount = 800m };
        advance.Adjust(100m);
        advance.Refund(300m);

        advance.Reverse(500m).Error!.Code.ShouldBe("billing.reversal_exceeds_held");
        advance.ReverseAdjustment(100m);
        advance.ReverseRefund(300m).IsSuccess.ShouldBeTrue();
        advance.ReverseRefund(1m).Error!.Code.ShouldBe("billing.refund_reversal_exceeds");
        advance.Reverse(800m).IsSuccess.ShouldBeTrue();
        (advance.Amount, advance.Balance).ShouldBe((0m, 0m));
    }

    [Fact]
    public void A_deposit_gives_up_only_what_it_still_holds_and_a_settled_stay_is_reopened()
    {
        var account = new IpAccount { AdmissionNumber = "NHAD260000007", Status = IpAccountStatus.Open };
        account.RecordDeposit(5_000m);
        account.AdjustDeposit(4_000m);

        account.ReverseDeposit(5_000m).Error!.Code.ShouldBe("billing.reversal_exceeds_held");
        account.ReverseAdjustment(4_000m);
        account.ReverseDeposit(5_000m).IsSuccess.ShouldBeTrue();
        account.DepositBalance.ShouldBe(0m);

        account.RecordDeposit(1_000m);
        account.RefundDeposit(1_000m);
        account.ReverseDepositRefund(1_000m).IsSuccess.ShouldBeTrue();
        account.DepositBalance.ShouldBe(1_000m);

        account.Status = IpAccountStatus.Closed;
        account.ReopenSettlement();
        account.Status.ShouldBe(IpAccountStatus.Discharged);
        account.Status = IpAccountStatus.Open;
        account.ReopenSettlement();
        account.Status.ShouldBe(IpAccountStatus.Open, "only a closed account is reopened");
    }

    [Fact]
    public void Insurers_settlements_adjustments_cash_and_lines_reversed_already_are_not_reversed()
    {
        ReversalRules.ReceiptRefusal(new Receipt { Purpose = ReceiptPurpose.PayerSettlement }).ShouldNotBeNull().Code.ShouldBe("billing.reversal_payer_settlement");
        ReversalRules.ReceiptRefusal(new Receipt { Purpose = ReceiptPurpose.BillPayment, Source = ReceiptSource.DepositAdjustment }).ShouldNotBeNull().Code
            .ShouldBe("billing.reversal_adjustment");
        ReversalRules.ReceiptRefusal(new Receipt { Purpose = ReceiptPurpose.BillPayment, Source = ReceiptSource.PaymentGateway }).ShouldBeNull();
        ReversalRules.ReceiptRefusal(new Receipt { Purpose = ReceiptPurpose.Deposit }).ShouldBeNull();
        ReversalRules.ReceiptRefusal(new Receipt { Purpose = ReceiptPurpose.Advance }).ShouldBeNull();

        ReversalRules.LineRefusal(Line(PaymentMode.Cash), 0m)!.Code.ShouldBe("billing.reversal_cash");
        ReversalRules.LineRefusal(Line(PaymentMode.Advance), 0m)!.Code.ShouldBe("billing.reversal_mode");
        ReversalRules.LineRefusal(Line(PaymentMode.Insurance), 0m)!.Code.ShouldBe("billing.reversal_mode");
        ReversalRules.LineRefusal(Line(PaymentMode.Cheque), 600m)!.Code.ShouldBe("billing.reversal_done");
        foreach (var mode in new[] { PaymentMode.Card, PaymentMode.Upi, PaymentMode.NetBanking, PaymentMode.Cheque, PaymentMode.PaymentGateway, PaymentMode.Wallet })
        {
            ReversalRules.LineRefusal(Line(mode), 0m).ShouldBeNull(mode.ToString());
            ReversalRules.LineRefusal(Line(mode), 599.99m).ShouldBeNull("part of it is left");
        }
    }

    [Fact]
    public void A_refund_is_recorded_as_not_paid_out_only_when_money_was_recorded_as_leaving_and_the_gateway_has_not_paid_it()
    {
        ReversalRules.RefundRefusal(Refund(RefundStatus.Approved, null))!.Code.ShouldBe("billing.refund_not_paid");
        ReversalRules.RefundRefusal(Refund(RefundStatus.Paid, PaymentMode.Advance))!.Code.ShouldBe("billing.refund_reversal_advance");
        ReversalRules.RefundRefusal(Refund(RefundStatus.Paid, PaymentMode.Cash))!.Code.ShouldBe("billing.refund_reversal_cash");
        ReversalRules.RefundRefusal(Refund(RefundStatus.Paid, PaymentMode.Cheque)).ShouldBeNull();
        ReversalRules.RefundRefusal(Refund(RefundStatus.Paid, PaymentMode.NetBanking)).ShouldBeNull();

        var processed = Gateway("rfnd_Q1", PaymentRefundStatus.Processed);
        ReversalRules.RefundRefusal(processed)!.Message.ShouldContain("processed");
        ReversalRules.RefundRefusal(Gateway("rfnd_Q2", PaymentRefundStatus.Pending))!.Code.ShouldBe("billing.refund_reversal_gateway");
        ReversalRules.RefundRefusal(Gateway("rfnd_Q3", PaymentRefundStatus.Failed)).ShouldBeNull();
        var retrying = Gateway("rfnd_Q4", PaymentRefundStatus.Failed);
        retrying.GatewayRetryStartedAt = Now;
        ReversalRules.RefundRefusal(retrying)!.Code.ShouldBe("billing.refund_reversal_gateway", "a retry is still in flight");
        ReversalRules.RefundRefusal(Gateway(TrainingSimulators.RefundIdPrefix + "a1b2c3", PaymentRefundStatus.Processed))
            .ShouldBeNull("the simulator returned no money, whatever it says");

        var undone = Refund(RefundStatus.Paid, PaymentMode.Cheque);
        undone.PayoutReversedAt = Now;
        ReversalRules.RefundRefusal(undone)!.Code.ShouldBe("billing.refund_reversal_done");
    }

    [Fact]
    public void A_refund_recorded_as_not_paid_out_is_never_sent_through_the_gateway_again()
    {
        var failed = Gateway("rfnd_Q5", PaymentRefundStatus.Failed);
        failed.CanSendGatewayRefundAgain(Now).ShouldBeTrue();

        failed.PayoutReversedAt = Now;

        failed.CanSendGatewayRefundAgain(Now).ShouldBeFalse();
        failed.ClaimGatewayRetry(Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_refund_waiting_to_be_paid_is_closed_when_its_money_is_gone_and_a_paid_one_is_left_alone()
    {
        var waiting = Refund(RefundStatus.Approved, null);
        var approver = Guid.CreateVersion7();

        waiting.CloseUnpaid(approver, "Closed by NHRR26000001: the money it would return was not received.", Now);

        (waiting.Status, waiting.DecidedBy, waiting.DecidedAt).ShouldBe((RefundStatus.Rejected, approver, Now));
        waiting.DecisionRemarks.ShouldBe("Closed by NHRR26000001: the money it would return was not received.");

        var paid = Refund(RefundStatus.Paid, PaymentMode.Cheque);
        paid.CloseUnpaid(approver, "x", Now);
        paid.Status.ShouldBe(RefundStatus.Paid);
    }

    [Fact]
    public void A_reversal_tells_other_modules_what_no_longer_counts()
    {
        var reversal = new ReceiptReversal
        {
            FacilityId = FacilityId, Number = "NHRR260000001", ReceiptId = Guid.CreateVersion7(), PatientId = PatientId, BillId = Guid.CreateVersion7(),
            Amount = 600m, ReversedAt = Now,
        };
        reversal.AnnouncePaymentReversed();
        var reversed = reversal.DomainEvents.OfType<PaymentReversed>().ShouldHaveSingleItem();
        (reversed.ReversalId, reversed.ReceiptId, reversed.Amount, reversed.BillId, reversed.ReversedAt)
            .ShouldBe((reversal.Id, reversal.ReceiptId, 600m, reversal.BillId, Now));

        var refund = new RefundReversal { FacilityId = FacilityId, Number = "NHRX260000001", RefundId = Guid.CreateVersion7(), PatientId = PatientId, Amount = 250m, ReversedAt = Now };
        refund.AnnounceRefundReversed();
        var payout = refund.DomainEvents.OfType<RefundPayoutReversed>().ShouldHaveSingleItem();
        (payout.RefundId, payout.Amount, payout.ReversalNumber).ShouldBe((refund.RefundId, 250m, "NHRX260000001"));
    }

    private static Bill PaidBill(decimal amount)
    {
        var bill = FinalizedBill(Charge(amount));
        bill.ApplyPayment(amount, allowExcess: false).IsSuccess.ShouldBeTrue();
        bill.Status.ShouldBe(BillStatus.Paid);
        bill.ClearDomainEvents();
        return bill;
    }

    private static ReceiptPayment Line(PaymentMode mode) => new() { Mode = mode, Amount = 600m };

    private static Refund Refund(RefundStatus status, PaymentMode? mode)
    {
        var refund = Domain.Refund.Request(FacilityId, "NHRF26000001", RefundPurpose.Bill, PatientId, Guid.CreateVersion7(), null, 600m, "Bill cancelled", Guid.CreateVersion7(), "Desk", Now);
        refund.Status = status;
        refund.Mode = mode;
        refund.PaidAt = status == RefundStatus.Paid ? Now : null;
        return refund;
    }

    private static Refund Gateway(string gatewayRefundId, PaymentRefundStatus status)
    {
        var refund = Refund(RefundStatus.Paid, PaymentMode.PaymentGateway);
        refund.StartGatewayRefund(gatewayRefundId, Now);
        refund.GatewayStatus = status;
        return refund;
    }
}
