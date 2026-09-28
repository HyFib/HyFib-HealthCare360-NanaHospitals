using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class RefundTests
{
    private static readonly Guid Requester = Guid.CreateVersion7();
    private static readonly Guid Approver = Guid.CreateVersion7();

    private static Refund NewRefund() => Refund.Request(
        TestData.FacilityId, "NHRF26000001", RefundPurpose.Bill, TestData.PatientId, Guid.CreateVersion7(), null, 500.555m, " Overpaid ", Requester, "Cashier", TestData.Now);

    [Fact]
    public void A_request_is_rounded_trimmed_and_outstanding()
    {
        var refund = NewRefund();

        refund.Amount.ShouldBe(500.56m);
        refund.Reason.ShouldBe("Overpaid");
        refund.Status.ShouldBe(RefundStatus.Requested);
        refund.IsOutstanding.ShouldBeTrue();
    }

    [Fact]
    public void The_requester_cannot_approve_their_own_refund()
    {
        var refund = NewRefund();

        refund.Approve(Requester, null, TestData.Now).Error!.Type.ShouldBe(ErrorType.Forbidden);
        refund.Status.ShouldBe(RefundStatus.Requested);
    }

    [Fact]
    public void Money_is_paid_out_only_after_approval()
    {
        var refund = NewRefund();

        refund.MarkPaid(PaymentMode.Cash, null, Requester, null, "OP-1", TestData.Now).Error!.Code.ShouldBe("billing.refund_not_approved");
        refund.Approve(Approver, "Verified", TestData.Now).IsSuccess.ShouldBeTrue();
        refund.MarkPaid(PaymentMode.Cash, null, Requester, Guid.CreateVersion7(), "OP-1", TestData.Now).IsSuccess.ShouldBeTrue();

        refund.Status.ShouldBe(RefundStatus.Paid);
        refund.IsOutstanding.ShouldBeFalse();
        refund.DecidedBy.ShouldBe(Approver);
    }

    [Fact]
    public void Non_cash_refunds_need_a_reference_and_cannot_be_advance_or_insurance()
    {
        var refund = NewRefund();
        refund.Approve(Approver, null, TestData.Now);

        refund.MarkPaid(PaymentMode.NetBanking, "  ", null, null, null, TestData.Now).Error!.Type.ShouldBe(ErrorType.Validation);
        refund.MarkPaid(PaymentMode.Advance, "X", null, null, null, TestData.Now).IsFailure.ShouldBeTrue();
        refund.MarkPaid(PaymentMode.Insurance, "X", null, null, null, TestData.Now).IsFailure.ShouldBeTrue();
        refund.MarkPaid(PaymentMode.Upi, "425612345678", null, null, null, TestData.Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_rejected_refund_is_closed()
    {
        var refund = NewRefund();

        refund.Reject(Approver, "Already refunded by cheque", TestData.Now).IsSuccess.ShouldBeTrue();

        refund.IsOutstanding.ShouldBeFalse();
        refund.DecisionRemarks.ShouldBe("Already refunded by cheque");
        refund.Approve(Approver, null, TestData.Now).Error!.Code.ShouldBe("billing.refund_not_pending");
    }

    [Fact]
    public void Restoring_an_advance_is_complete_at_once_and_moves_no_money()
    {
        var refund = Refund.ReturnToAdvance(TestData.FacilityId, "NHRF26000002", TestData.PatientId, Guid.CreateVersion7(), Guid.CreateVersion7(), 2_000m, "Bill cancelled", Requester, TestData.Now);

        refund.Status.ShouldBe(RefundStatus.Paid);
        refund.Mode.ShouldBe(PaymentMode.Advance);
        refund.IsOutstanding.ShouldBeFalse();
    }
}
