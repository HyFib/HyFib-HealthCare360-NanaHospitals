using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Discounts with approval limits (MB-05, OPB-04). Staff may discount up to their own limit; above it the request is
/// parked as a <see cref="DiscountApproval"/> and approvers are alerted. The bill changes only when a second person
/// (holding <c>billing.discounts.approve</c>) approves, and finalization re-checks the sanctioned amount.
/// </summary>
public sealed class DiscountService(
    HospitalDbContext db,
    IPermissionService permissions,
    ISettingsService settings,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>Discount % a user without a personal limit may give (default 0: every discount needs approval).</summary>
    public const string DefaultLimitSetting = "billing.discount.defaultMaxPercent";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DiscountAuthority> AuthorityAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return DiscountAuthority.None;
        }

        if (await permissions.HasPermissionAsync(userId, BillingPermissions.DiscountsApprove, ct))
        {
            return new DiscountAuthority(true, 100m, null);
        }

        var limit = await db.Set<DiscountLimit>().AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId, ct);
        if (limit is not null)
        {
            return new DiscountAuthority(false, limit.MaxPercent, limit.MaxAmount);
        }

        var fallback = await settings.GetAsync<decimal?>(DefaultLimitSetting, currentUser.FacilityId, ct) ?? 0m;
        return new DiscountAuthority(false, Math.Clamp(fallback, 0m, 100m), null);
    }

    public async Task<Result<DiscountOutcome>> ApplyAsync(Guid billId, ApplyDiscountRequest request, CancellationToken ct)
    {
        var bill = await BillQueries.LoadAsync(db, billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        if (bill.Status != BillStatus.Draft)
        {
            return Error.Rule("billing.bill_not_draft", "Discounts can be changed only on draft bills.");
        }

        db.GuardVersion(bill, request.Version);
        var lineAmounts = ResolveLineDiscounts(bill, request.LineDiscounts);
        if (lineAmounts.IsFailure)
        {
            return lineAmounts.Error!;
        }

        var preview = Preview(bill, request.BillDiscountPercent, request.BillDiscountAmount, lineAmounts.Value);
        var authority = await AuthorityAsync(ct);
        var allowed = DiscountPolicy.AllowedWithoutApproval(preview.DiscountBase, authority);
        var now = time.GetUtcNow();
        await WithdrawPendingAsync(billId, now, ct);

        if (!DiscountPolicy.RequiresApproval(preview.UserDiscount, preview.DiscountBase, authority, bill.ApprovedDiscountAmount))
        {
            bill.SetUserDiscounts(request.BillDiscountPercent, request.BillDiscountAmount, lineAmounts.Value, request.Reason);
            var totals = bill.Recalculate();
            if (authority.CanApprove && totals.UserDiscount > 0 && currentUser.UserId is { } approver)
            {
                bill.RecordDiscountApproval(approver, totals.UserDiscount);
            }

            await db.SaveChangesAsync(ct);
            return new DiscountOutcome(true, null, totals.UserDiscount, allowed);
        }

        var approval = new DiscountApproval
        {
            FacilityId = bill.FacilityId,
            BillId = bill.Id,
            PatientId = bill.PatientId,
            RequestedBy = currentUser.UserId,
            RequestedByName = currentUser.DisplayName ?? currentUser.UserName,
            RequestedAt = now,
            BillDiscountPercent = request.BillDiscountPercent is > 0 ? request.BillDiscountPercent : null,
            BillDiscountAmount = request.BillDiscountPercent is > 0 ? null : request.BillDiscountAmount,
            LineDiscountsJson = JsonSerializer.Serialize(lineAmounts.Value, Json),
            RequestedAmount = preview.UserDiscount,
            DiscountBase = preview.DiscountBase,
            Reason = request.Reason.Trim(),
            Status = DiscountApprovalStatus.Pending,
        };
        db.Add(approval);
        await db.SaveChangesAsync(ct);

        await notifications.NotifyPermissionHoldersAsync(
            BillingPermissions.DiscountsApprove,
            bill.FacilityId,
            "Discount approval needed",
            $"{approval.RequestedByName ?? "A cashier"} requested a discount of ₹{Money.FormatInr(approval.RequestedAmount)} ({approval.Reason}).",
            $"/billing/discount-approvals/{approval.Id}",
            "billing",
            "warning",
            ct);
        return new DiscountOutcome(false, approval.Id, preview.UserDiscount, allowed);
    }

    public async Task<Result> ApproveAsync(Guid approvalId, string? remarks, CancellationToken ct)
    {
        var approval = await db.Set<DiscountApproval>().FirstOrDefaultAsync(a => a.Id == approvalId, ct);
        if (approval is null)
        {
            return Error.NotFound(nameof(DiscountApproval), approvalId);
        }

        if (currentUser.UserId is not { } approverId)
        {
            return BillingDb.SignInRequired;
        }

        var now = time.GetUtcNow();
        var bill = await BillQueries.LoadAsync(db, approval.BillId, ct);
        if (bill is null || bill.Status != BillStatus.Draft)
        {
            approval.Withdraw(now);
            await db.SaveChangesAsync(ct);
            return Error.Rule("billing.discount_bill_closed", "The bill is no longer a draft, so the request was withdrawn.");
        }

        var decided = approval.Approve(approverId, remarks, now);
        if (decided.IsFailure)
        {
            return decided;
        }

        var lines = JsonSerializer.Deserialize<Dictionary<Guid, decimal>>(approval.LineDiscountsJson, Json) ?? new Dictionary<Guid, decimal>();
        bill.SetUserDiscounts(approval.BillDiscountPercent, approval.BillDiscountAmount, lines, approval.Reason);
        bill.Recalculate();

        // Sanction exactly what the approver saw: if the draft changed since, a larger discount needs a new approval.
        bill.RecordDiscountApproval(approverId, approval.RequestedAmount);
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.discount.approved", $"Approved a discount of ₹{Money.FormatInr(approval.RequestedAmount)}: {approval.Reason}")
        {
            PatientId = bill.PatientId,
            EntityType = nameof(Bill),
            EntityId = bill.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval.Id, approval.RequestedBy, approval.RequestedAmount, approval.DiscountBase },
        });
        await db.SaveChangesAsync(ct);
        await NotifyRequesterAsync(approval, "Discount approved", ct);
        return Result.Success();
    }

    public async Task<Result> RejectAsync(Guid approvalId, string? remarks, CancellationToken ct)
    {
        var approval = await db.Set<DiscountApproval>().FirstOrDefaultAsync(a => a.Id == approvalId, ct);
        if (approval is null)
        {
            return Error.NotFound(nameof(DiscountApproval), approvalId);
        }

        if (currentUser.UserId is not { } approverId)
        {
            return BillingDb.SignInRequired;
        }

        var decided = approval.Reject(approverId, remarks, time.GetUtcNow());
        if (decided.IsFailure)
        {
            return decided;
        }

        await db.SaveChangesAsync(ct);
        await NotifyRequesterAsync(approval, "Discount request declined", ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<DiscountLimitDto>> ListLimitsAsync(CancellationToken ct)
    {
        var limits = await db.Set<DiscountLimit>().AsNoTracking().ToListAsync(ct);
        var userIds = limits.Select(l => l.UserId).ToList();
        var names = await db.Set<User>().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return limits
            .Select(l => new DiscountLimitDto(l.Id, l.UserId, names.GetValueOrDefault(l.UserId), l.MaxPercent, l.MaxAmount, l.Notes, l.Version))
            .OrderBy(l => l.UserName)
            .ToList();
    }

    public async Task<Result> SaveLimitAsync(SaveDiscountLimitRequest request, CancellationToken ct)
    {
        if (!await db.Set<User>().AnyAsync(u => u.Id == request.UserId && u.IsActive, ct))
        {
            return Error.Field("userId", "Choose an active user.");
        }

        var limit = await db.Set<DiscountLimit>().FirstOrDefaultAsync(l => l.UserId == request.UserId, ct);
        if (limit is null)
        {
            limit = new DiscountLimit { UserId = request.UserId };
            db.Add(limit);
        }

        db.SetExpectedVersion(limit, request.Version);
        limit.MaxPercent = request.MaxPercent;
        limit.MaxAmount = request.MaxAmount;
        limit.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> DeleteLimitAsync(Guid id, CancellationToken ct)
    {
        var limit = await db.Set<DiscountLimit>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (limit is null)
        {
            return Error.NotFound(nameof(DiscountLimit), id);
        }

        db.Remove(limit);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Converts requested line discounts (percent or rupees) into rupees and checks them against each line.</summary>
    internal static Result<Dictionary<Guid, decimal>> ResolveLineDiscounts(Bill bill, IReadOnlyList<LineDiscountInput>? inputs)
    {
        var amounts = new Dictionary<Guid, decimal>();
        foreach (var input in inputs ?? [])
        {
            var line = bill.Lines.FirstOrDefault(l => l.Id == input.LineId);
            if (line is null)
            {
                return Error.Field("lineDiscounts", "A discounted service is not on this bill.");
            }

            if (!line.IsDiscountAllowed)
            {
                return Error.Field("lineDiscounts", $"{line.Description} cannot be discounted.");
            }

            var amount = input.Percent is { } percent
                ? Money.PercentOf(line.DiscountableAmount, Math.Clamp(percent, 0m, 100m))
                : Money.Round(Math.Max(input.Amount ?? 0m, 0m));
            if (amount > line.DiscountableAmount)
            {
                return Error.Field("lineDiscounts", $"The discount on {line.Description} is more than its value.");
            }

            if (amount > 0)
            {
                amounts[line.Id] = amount;
            }
        }

        return amounts;
    }

    /// <summary>Totals the bill would have with the requested discounts, without changing it.</summary>
    internal static BillTotals Preview(Bill bill, decimal? billPercent, decimal? billAmount, IReadOnlyDictionary<Guid, decimal> lineAmounts)
    {
        var ordered = bill.Lines.OrderBy(l => l.SortOrder).ToList();
        var inputs = ordered.Select(l => l.ToInput() with { LineDiscount = lineAmounts.GetValueOrDefault(l.Id) }).ToList();
        var discount = billPercent is > 0 ? new BillDiscountInput(billPercent, null) : new BillDiscountInput(null, billAmount);
        return BillTotalsCalculator.Calculate(inputs, discount, new SplitTerms(bill.HasPayer, bill.CoPayPercent, bill.PayerCoverageLimit));
    }

    private async Task WithdrawPendingAsync(Guid billId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var pending in await db.Set<DiscountApproval>().Where(a => a.BillId == billId && a.Status == DiscountApprovalStatus.Pending).ToListAsync(ct))
        {
            pending.Withdraw(now);
        }
    }

    private Task NotifyRequesterAsync(DiscountApproval approval, string title, CancellationToken ct) =>
        approval.RequestedBy is { } requester
            ? notifications.NotifyUsersAsync([requester], title, approval.Remarks ?? approval.Reason, $"/billing/bills/{approval.BillId}", "billing", "info", ct)
            : Task.CompletedTask;
}
