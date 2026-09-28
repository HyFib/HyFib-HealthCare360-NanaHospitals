namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum DiscountApprovalStatus
{
    Pending = 0,
    Approved,
    Rejected,

    /// <summary>Superseded by a newer request, or the bill was discarded or finalized meanwhile.</summary>
    Withdrawn,
}

/// <summary>How much discount a user may give on their own (MB-05, OPB-04).</summary>
public sealed record DiscountAuthority(bool CanApprove, decimal MaxPercent, decimal? MaxAmount)
{
    public static DiscountAuthority None { get; } = new(false, 0m, 0m);
}

/// <summary>
/// Discount approval rule: staff may discount up to their percentage (and optional rupee) limit of the discountable
/// value; anything above needs someone holding <c>billing.discounts.approve</c>, whose sanctioned amount then covers it.
/// </summary>
public static class DiscountPolicy
{
    public static decimal AllowedWithoutApproval(decimal discountBase, DiscountAuthority authority)
    {
        if (authority.CanApprove)
        {
            return Math.Max(discountBase, 0m);
        }

        var limit = Money.PercentOf(Math.Max(discountBase, 0m), Math.Clamp(authority.MaxPercent, 0m, 100m));
        return authority.MaxAmount is { } cap ? Math.Min(limit, Math.Max(cap, 0m)) : limit;
    }

    public static bool RequiresApproval(decimal userDiscount, decimal discountBase, DiscountAuthority authority, decimal approvedAmount) =>
        userDiscount > 0
        && !authority.CanApprove
        && userDiscount > approvedAmount
        && userDiscount > AllowedWithoutApproval(discountBase, authority);
}

/// <summary>Per-user discount ceiling set by administrators (users without one get the facility default, 0% unless configured).</summary>
public sealed class DiscountLimit : AuditableEntity
{
    public Guid UserId { get; set; }
    public decimal MaxPercent { get; set; }
    public decimal? MaxAmount { get; set; }
    public string? Notes { get; set; }
}

/// <summary>A discount above the requester's limit, parked until an approver decides; the bill is unchanged meanwhile.</summary>
public sealed class DiscountApproval : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid BillId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? RequestedBy { get; set; }
    public string? RequestedByName { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public decimal? BillDiscountPercent { get; set; }
    public decimal? BillDiscountAmount { get; set; }

    /// <summary>Requested line discounts in rupees, as JSON (<c>{ "lineId": amount }</c>).</summary>
    public string LineDiscountsJson { get; set; } = "{}";

    /// <summary>Total staff discount the request would give (for the approver's screen and the audit trail).</summary>
    public decimal RequestedAmount { get; set; }

    public decimal DiscountBase { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DiscountApprovalStatus Status { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Remarks { get; set; }

    public Result Approve(Guid approverId, string? remarks, DateTimeOffset now) => Decide(approverId, remarks, now, DiscountApprovalStatus.Approved);

    public Result Reject(Guid approverId, string? remarks, DateTimeOffset now) => Decide(approverId, remarks, now, DiscountApprovalStatus.Rejected);

    public void Withdraw(DateTimeOffset now)
    {
        if (Status == DiscountApprovalStatus.Pending)
        {
            Status = DiscountApprovalStatus.Withdrawn;
            DecidedAt = now;
        }
    }

    private Result Decide(Guid approverId, string? remarks, DateTimeOffset now, DiscountApprovalStatus outcome)
    {
        if (Status != DiscountApprovalStatus.Pending)
        {
            return Error.Rule("billing.discount_not_pending", "This discount request has already been decided.");
        }

        if (RequestedBy == approverId)
        {
            return Error.Forbidden("billing.discount_self_approval", "A discount must be approved by someone other than the person who requested it.");
        }

        Status = outcome;
        DecidedBy = approverId;
        DecidedAt = now;
        Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
        return Result.Success();
    }
}
