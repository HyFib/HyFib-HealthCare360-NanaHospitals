using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class DiscountPolicyTests
{
    private static readonly DiscountAuthority TenPercent = new(false, 10m, null);

    [Fact]
    public void A_discount_within_the_users_limit_needs_no_approval()
    {
        DiscountPolicy.RequiresApproval(100m, 1_000m, TenPercent, approvedAmount: 0m).ShouldBeFalse();
    }

    [Fact]
    public void A_discount_above_the_users_limit_needs_approval()
    {
        DiscountPolicy.RequiresApproval(100.01m, 1_000m, TenPercent, approvedAmount: 0m).ShouldBeTrue();
    }

    [Fact]
    public void The_rupee_cap_applies_when_it_is_lower_than_the_percentage()
    {
        var capped = new DiscountAuthority(false, 20m, 500m);

        DiscountPolicy.AllowedWithoutApproval(10_000m, capped).ShouldBe(500m);
        DiscountPolicy.RequiresApproval(600m, 10_000m, capped, 0m).ShouldBeTrue();
    }

    [Fact]
    public void Approvers_are_not_limited()
    {
        DiscountPolicy.RequiresApproval(900m, 1_000m, new DiscountAuthority(true, 100m, null), 0m).ShouldBeFalse();
    }

    [Fact]
    public void A_sanctioned_amount_covers_the_discount_but_not_more()
    {
        DiscountPolicy.RequiresApproval(300m, 1_000m, TenPercent, approvedAmount: 300m).ShouldBeFalse();
        DiscountPolicy.RequiresApproval(301m, 1_000m, TenPercent, approvedAmount: 300m).ShouldBeTrue();
    }

    [Fact]
    public void Without_a_limit_every_discount_needs_approval_but_no_discount_never_does()
    {
        DiscountPolicy.RequiresApproval(1m, 1_000m, DiscountAuthority.None, 0m).ShouldBeTrue();
        DiscountPolicy.RequiresApproval(0m, 1_000m, DiscountAuthority.None, 0m).ShouldBeFalse();
    }

    [Fact]
    public void The_requester_cannot_approve_their_own_discount()
    {
        var requester = Guid.CreateVersion7();
        var approval = new DiscountApproval { RequestedBy = requester, Status = DiscountApprovalStatus.Pending };

        var result = approval.Approve(requester, null, TestData.Now);

        result.Error!.Type.ShouldBe(ErrorType.Forbidden);
        approval.Status.ShouldBe(DiscountApprovalStatus.Pending);
    }

    [Fact]
    public void A_decided_request_cannot_be_decided_again()
    {
        var approval = new DiscountApproval { RequestedBy = Guid.CreateVersion7(), Status = DiscountApprovalStatus.Pending };

        approval.Reject(Guid.CreateVersion7(), "Not justified", TestData.Now).IsSuccess.ShouldBeTrue();
        approval.Approve(Guid.CreateVersion7(), null, TestData.Now).Error!.Code.ShouldBe("billing.discount_not_pending");
        approval.Remarks.ShouldBe("Not justified");
    }

    [Fact]
    public void Withdrawing_affects_only_pending_requests()
    {
        var pending = new DiscountApproval { RequestedBy = Guid.CreateVersion7(), Status = DiscountApprovalStatus.Pending };
        var approved = new DiscountApproval { RequestedBy = Guid.CreateVersion7(), Status = DiscountApprovalStatus.Approved };

        pending.Withdraw(TestData.Now);
        approved.Withdraw(TestData.Now);

        pending.Status.ShouldBe(DiscountApprovalStatus.Withdrawn);
        approved.Status.ShouldBe(DiscountApprovalStatus.Approved);
    }
}
