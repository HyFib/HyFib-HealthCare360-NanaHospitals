namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum DiscountApprovalStatus
{
    Pending = 0,
    Approved,
    Rejected,

    /// <summary>Superseded by a newer request, or the bill was discarded or finalized meanwhile.</summary>
    Withdrawn,
}

/// <summary>
/// How much discount a user may give on their own (MB-05, OPB-04): their own limit, or the facility default. Holding the
/// approval permission gives no more: an approver's own discount above their limit waits for another approver
/// (MAKER-CHECKER.md, slice 6).
/// </summary>
public sealed record DiscountAuthority(decimal MaxPercent, decimal? MaxAmount)
{
    public static DiscountAuthority None { get; } = new(0m, 0m);
}

/// <summary>
/// Discount approval rule: staff may discount up to their percentage (and optional rupee) limit of the discountable
/// value; anything above needs someone else holding <c>billing.discounts.approve</c>, whose sanctioned amount then covers
/// it. Approvers are held to their own limit like everyone else.
/// </summary>
public static class DiscountPolicy
{
    public static decimal AllowedWithoutApproval(decimal discountBase, DiscountAuthority authority)
    {
        var limit = Money.PercentOf(Math.Max(discountBase, 0m), Math.Clamp(authority.MaxPercent, 0m, 100m));
        return authority.MaxAmount is { } cap ? Math.Min(limit, Math.Max(cap, 0m)) : limit;
    }

    public static bool RequiresApproval(decimal userDiscount, decimal discountBase, DiscountAuthority authority, decimal approvedAmount) =>
        userDiscount > 0
        && userDiscount > approvedAmount
        && userDiscount > AllowedWithoutApproval(discountBase, authority);

    /// <summary>
    /// Whether going from the limit in force (<paramref name="before"/>) to <paramref name="asked"/> lets the user give
    /// more on their own: a higher percentage, or a rupee cap raised or taken away. Anything else only narrows it.
    /// </summary>
    public static bool Raises(DiscountAuthority before, DiscountAuthority asked)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(asked);
        var percentUp = Math.Clamp(asked.MaxPercent, 0m, 100m) > Math.Clamp(before.MaxPercent, 0m, 100m);
        var capUp = before.MaxAmount is { } cap && (asked.MaxAmount is not { } next || next > cap);
        return percentUp || capUp;
    }

    /// <summary>A limit in words for the approver: "10%", "10%, at most ₹500.00".</summary>
    public static string Words(DiscountAuthority limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        var percent = limit.MaxPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
        return limit.MaxAmount is { } cap ? $"{percent}, at most ₹{Money.FormatInr(cap)}" : percent;
    }
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
