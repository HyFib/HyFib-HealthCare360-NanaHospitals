using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class InsuranceClaimTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static InsuranceClaim NewClaim(decimal claimed = 100_000m) =>
        new() { ClaimNumber = "NHCL26000001", ClaimedAmount = claimed, Status = ClaimStatus.Submitted, SubmittedAt = TestData.Now };

    [Fact]
    public void Settlement_with_deductions_that_clears_the_claim_marks_it_settled()
    {
        var claim = NewClaim();

        var result = claim.Settle(92_000m, [new DeductionInput("Non-medical consumables", 5_000m), new DeductionInput("Room rent capping", 3_000m)], "UTR0001", null, Today, TestData.Now);

        result.IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.Settled);
        claim.SettledAmount.ShouldBe(92_000m);
        claim.DeductionAmount.ShouldBe(8_000m);
        claim.Outstanding.ShouldBe(0m);
        claim.Deductions.Count.ShouldBe(2);
        result.Value.DeductionAmount.ShouldBe(8_000m);
    }

    [Fact]
    public void A_partial_tranche_leaves_the_claim_partially_settled_and_can_be_followed_by_the_rest()
    {
        var claim = NewClaim();

        claim.Settle(60_000m, [], "UTR1", null, Today, TestData.Now).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.PartiallySettled);
        claim.Outstanding.ShouldBe(40_000m);

        claim.Settle(40_000m, [], "UTR2", null, Today, TestData.Now).IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.Settled);
        claim.Settlements.Count.ShouldBe(2);
    }

    [Fact]
    public void Settlement_cannot_exceed_the_outstanding_amount()
    {
        NewClaim().Settle(90_000m, [new DeductionInput("Other", 10_000.01m)], "UTR", null, Today, TestData.Now)
            .Error!.Code.ShouldBe("insurance.settlement_exceeds");
    }

    [Fact]
    public void Money_needs_a_utr_and_deductions_need_reasons()
    {
        var claim = NewClaim();

        claim.Settle(10_000m, [], utrNumber: null, null, Today, TestData.Now).IsFailure.ShouldBeTrue();
        claim.Settle(0m, [new DeductionInput(" ", 100m)], null, null, Today, TestData.Now).IsFailure.ShouldBeTrue();
        claim.Settle(0m, [new DeductionInput("Disallowed", 100m)], null, null, Today, TestData.Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Queries_return_the_claim_to_where_it_was()
    {
        var claim = NewClaim();
        claim.RaiseQuery("Discharge summary missing", TestData.Now).IsSuccess.ShouldBeTrue();
        claim.Settle(1m, [], "UTR", null, Today, TestData.Now).IsFailure.ShouldBeTrue();
        claim.AnswerQuery("Uploaded", TestData.Now);
        claim.Status.ShouldBe(ClaimStatus.Submitted);

        claim.Approve(95_000m, TestData.Now).IsSuccess.ShouldBeTrue();
        claim.RaiseQuery("Clarify implant invoice", TestData.Now);
        claim.AnswerQuery("Invoice attached", TestData.Now);
        claim.Status.ShouldBe(ClaimStatus.Approved);
    }

    [Fact]
    public void Approval_cannot_exceed_the_claimed_amount()
    {
        NewClaim().Approve(100_000.01m, TestData.Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Rejection_disallows_the_whole_unpaid_balance()
    {
        var claim = NewClaim();

        var result = claim.Reject("Pre-existing disease exclusion", Today, TestData.Now);

        result.IsSuccess.ShouldBeTrue();
        claim.Status.ShouldBe(ClaimStatus.Rejected);
        result.Value!.DeductionAmount.ShouldBe(100_000m);
        claim.Outstanding.ShouldBe(0m);
        claim.Deductions.Single().Reason.ShouldStartWith("Claim rejected");
    }

    [Fact]
    public void A_claim_with_settlements_cannot_be_rejected()
    {
        var claim = NewClaim();
        claim.Settle(50_000m, [], "UTR", null, Today, TestData.Now);

        claim.Reject("late", Today, TestData.Now).IsFailure.ShouldBeTrue();
    }
}
