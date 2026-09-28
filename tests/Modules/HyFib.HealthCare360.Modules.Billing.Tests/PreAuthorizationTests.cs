using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class PreAuthorizationTests
{
    private static PreAuthorization Submitted(decimal requested = 100_000m)
    {
        var preAuth = new PreAuthorization { Number = "NHPA26000001", RequestedAmount = requested, Status = PreAuthStatus.Draft };
        preAuth.Submit(TestData.Now).IsSuccess.ShouldBeTrue();
        return preAuth;
    }

    [Fact]
    public void A_draft_needs_an_estimate_before_it_is_submitted()
    {
        var preAuth = new PreAuthorization { Status = PreAuthStatus.Draft };

        preAuth.Submit(TestData.Now).Error!.Code.ShouldBe("insurance.preauth_amount");
    }

    [Fact]
    public void A_query_is_answered_back_into_submitted()
    {
        var preAuth = Submitted();

        preAuth.RaiseQuery("Send the CT report", TestData.Now).IsSuccess.ShouldBeTrue();
        preAuth.Status.ShouldBe(PreAuthStatus.Query);
        preAuth.AnswerQuery("Attached", TestData.Now).IsSuccess.ShouldBeTrue();
        preAuth.Status.ShouldBe(PreAuthStatus.Submitted);
    }

    [Theory]
    [InlineData(100000, PreAuthStatus.Approved)]
    [InlineData(80000, PreAuthStatus.PartiallyApproved)]
    public void Approval_below_the_request_is_partial(decimal approved, PreAuthStatus expected)
    {
        var preAuth = Submitted();

        preAuth.Approve(approved, "AUTH-9", TestData.Now).IsSuccess.ShouldBeTrue();

        preAuth.Status.ShouldBe(expected);
        preAuth.EffectiveApprovedAmount.ShouldBe(approved);
        preAuth.ApprovalReference.ShouldBe("AUTH-9");
    }

    [Fact]
    public void Only_pending_requests_can_be_approved_or_rejected()
    {
        var draft = new PreAuthorization { Status = PreAuthStatus.Draft, RequestedAmount = 10m };

        draft.Approve(10m, null, TestData.Now).IsFailure.ShouldBeTrue();
        draft.Reject("x", TestData.Now).IsFailure.ShouldBeTrue();
        Submitted().Reject("Not covered: cosmetic", TestData.Now).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Approved_enhancements_add_to_the_effective_approval()
    {
        var preAuth = Submitted();
        preAuth.Approve(60_000m, null, TestData.Now);

        var enhancement = preAuth.RequestEnhancement(25_000m, "ICU stay extended", TestData.Now);
        enhancement.IsSuccess.ShouldBeTrue();
        preAuth.RequestEnhancement(5_000m, "Again", TestData.Now).Error!.Code.ShouldBe("insurance.enhancement_pending");
        preAuth.RespondToEnhancement(enhancement.Value.Id, approved: true, 20_000m, "ENH-1", TestData.Now).IsSuccess.ShouldBeTrue();

        preAuth.EffectiveApprovedAmount.ShouldBe(80_000m);
    }

    [Fact]
    public void Rejected_enhancements_add_nothing()
    {
        var preAuth = Submitted();
        preAuth.Approve(60_000m, null, TestData.Now);
        var enhancement = preAuth.RequestEnhancement(25_000m, "Extra days", TestData.Now);

        preAuth.RespondToEnhancement(enhancement.Value.Id, approved: false, 0m, null, TestData.Now).IsSuccess.ShouldBeTrue();

        preAuth.EffectiveApprovedAmount.ShouldBe(60_000m);
    }

    [Fact]
    public void Enhancements_need_an_approved_request()
    {
        Submitted().RequestEnhancement(1_000m, "x", TestData.Now).Error!.Code.ShouldBe("insurance.preauth_not_approved");
    }

    [Fact]
    public void The_final_approval_replaces_the_running_approvals()
    {
        var preAuth = Submitted();
        preAuth.Approve(60_000m, "AUTH-1", TestData.Now);

        preAuth.RecordFinalApproval(72_500m, "FINAL-1", TestData.Now).IsSuccess.ShouldBeTrue();

        preAuth.EffectiveApprovedAmount.ShouldBe(72_500m);
        preAuth.ApprovalReference.ShouldBe("FINAL-1");
        preAuth.RequestEnhancement(1_000m, "late", TestData.Now).Error!.Code.ShouldBe("insurance.preauth_final");
    }
}
