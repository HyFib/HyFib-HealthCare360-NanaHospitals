using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class PayoutCalculatorTests
{
    [Fact]
    public void Tds_is_deducted_on_the_gross_fee_and_recoveries_after_tax()
    {
        var amounts = PayoutCalculator.Calculate(50_000m, 2_000m, 10m);

        amounts.Tds.ShouldBe(5_000m);
        amounts.Net.ShouldBe(43_000m);
    }

    [Fact]
    public void Tds_rounds_half_away_from_zero()
    {
        var amounts = PayoutCalculator.Calculate(12_345.65m, 0m, 10m);

        amounts.Tds.ShouldBe(1_234.57m);
        amounts.Net.ShouldBe(11_111.08m);
    }

    [Fact]
    public void Deductions_larger_than_the_fees_give_a_negative_net()
    {
        PayoutCalculator.Calculate(1_000m, 1_000m, 10m).Net.ShouldBe(-100m);
    }

    [Fact]
    public void Reversals_alone_attract_no_tds()
    {
        var amounts = PayoutCalculator.Calculate(-500m, 0m, 10m);

        amounts.Tds.ShouldBe(0m);
        amounts.Net.ShouldBe(-500m);
    }

    [Fact]
    public void The_preparer_cannot_approve_their_own_payout()
    {
        var preparer = Guid.CreateVersion7();
        var payout = new DoctorPayout { CreatedBy = preparer, Status = PayoutStatus.Draft };

        payout.Approve(preparer, TestData.Now).Error!.Type.ShouldBe(ErrorType.Forbidden);
        payout.Approve(Guid.CreateVersion7(), TestData.Now).IsSuccess.ShouldBeTrue();
        payout.Status.ShouldBe(PayoutStatus.Approved);
    }

    [Fact]
    public void A_payout_is_paid_only_after_approval_and_with_a_reference_for_bank_payments()
    {
        var payout = new DoctorPayout { CreatedBy = Guid.CreateVersion7(), Status = PayoutStatus.Draft };

        payout.MarkPaid(PaymentMode.NetBanking, "UTR123", TestData.Now).Error!.Code.ShouldBe("doctorfees.payout_not_approved");
        payout.Approve(Guid.CreateVersion7(), TestData.Now);
        payout.MarkPaid(PaymentMode.NetBanking, null, TestData.Now).Error!.Type.ShouldBe(ErrorType.Validation);
        payout.MarkPaid(PaymentMode.Advance, "X", TestData.Now).IsFailure.ShouldBeTrue();
        payout.MarkPaid(PaymentMode.NetBanking, " UTR123 ", TestData.Now).IsSuccess.ShouldBeTrue();

        payout.Status.ShouldBe(PayoutStatus.Paid);
        payout.PaidReference.ShouldBe("UTR123");
    }
}
