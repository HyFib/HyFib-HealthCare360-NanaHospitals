using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Discounts with approval limits (MB-05, OPB-04). Staff may discount up to their own limit; above it the request is
/// parked as a <see cref="DiscountApproval"/> and approvers are alerted. The bill changes only when a second person
/// (holding <c>billing.discounts.approve</c>) approves, and finalization re-checks the sanctioned amount. Approvers are
/// held to their own limit too, and never approve their own discount (MAKER-CHECKER.md, slice 6). Raising anyone's limit
/// waits for a second approver, never the one whose limit it is (<see cref="LimitKind"/>); lowering it is made at once.
/// </summary>
public sealed class DiscountService(
    HospitalDbContext db,
    IApprovals approvals,
    ISettingsService settings,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>Discount % a user without a personal limit may give (default 0: every discount needs approval).</summary>
    public const string DefaultLimitSetting = "billing.discount.defaultMaxPercent";

    /// <summary>A user's own-authority discount limit raised (or their own limit removed, so a higher default applies).</summary>
    public const string LimitKind = "billing.discount_limit";

    /// <summary>Nobody asks for, or approves, a higher discount limit of their own.</summary>
    public const string OwnLimitCode = "billing.discount_limit_own";

    private const string NoReason = "No reason given.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The signed-in user's own discount authority: their limit, or the facility default. Holding
    /// <c>billing.discounts.approve</c> gives no more, so an approver's discount above it waits for another approver.
    /// </summary>
    public async Task<DiscountAuthority> AuthorityAsync(CancellationToken ct) =>
        currentUser.UserId is { } userId ? await AuthorityOfAsync(userId, ct) : DiscountAuthority.None;

    private async Task<DiscountAuthority> AuthorityOfAsync(Guid userId, CancellationToken ct)
    {
        var limit = await db.Set<DiscountLimit>().AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId, ct);
        return limit is not null ? new DiscountAuthority(limit.MaxPercent, limit.MaxAmount) : await DefaultAuthorityAsync(ct);
    }

    private async Task<DiscountAuthority> DefaultAuthorityAsync(CancellationToken ct)
    {
        var fallback = await settings.GetAsync<decimal?>(DefaultLimitSetting, currentUser.FacilityId, ct) ?? 0m;
        return new DiscountAuthority(Math.Clamp(fallback, 0m, 100m), null);
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

        if (bill.FreeTreatmentReason is not null)
        {
            return Error.Rule("billing.free_treatment", "This bill is free of cost by law, so there is nothing to discount.");
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
            // Given within the user's own limit (or an amount already sanctioned): nobody approved it, and nobody is
            // recorded as its approver.
            var totals = bill.Recalculate();
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

    /// <summary>The limits raised or removed that wait for a second approver, oldest first.</summary>
    public async Task<IReadOnlyList<PendingDiscountLimitDto>> PendingLimitsAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var waiting = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == LimitKind && r.SubjectType == nameof(DiscountLimit) && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Payload, r.RequestedBy, r.RequestedByName, r.RequestedAt })
            .ToListAsync(ct);
        var asked = waiting.Select(r => (Request: r, Payload: ApprovalPayload.Read<DiscountLimitPayload>(r.Payload))).ToList();
        var userIds = asked.Select(a => a.Payload.UserId).Distinct().ToList();
        var names = await db.Set<User>().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return asked
            .Select(a => new PendingDiscountLimitDto(
                a.Request.Id, a.Payload.UserId, names.GetValueOrDefault(a.Payload.UserId), DiscountPolicy.Words(a.Payload.Before),
                a.Payload.Asked is { } limit ? DiscountPolicy.Words(limit) : $"the default ({DiscountPolicy.Words(a.Payload.After)})",
                a.Request.RequestedByName, a.Request.RequestedAt, a.Request.RequestedBy == currentUser.UserId))
            .ToList();
    }

    /// <summary>
    /// Sets a user's own-authority limit. Lowering it is made at once; raising it (a higher percentage, a rupee cap raised
    /// or taken away) waits for another approver, who is never the user whose limit it is (<see cref="LimitKind"/>), and
    /// nobody asks to raise their own.
    /// </summary>
    public async Task<Result<DiscountLimitOutcome>> SaveLimitAsync(SaveDiscountLimitRequest request, CancellationToken ct)
    {
        if (!await db.Set<User>().AnyAsync(u => u.Id == request.UserId && u.IsActive, ct))
        {
            return Error.Field("userId", "Choose an active user.");
        }

        // A limit saved over one the user did not see (or over one created meanwhile) is refused, asked or not (409).
        var limit = await db.Set<DiscountLimit>().FirstOrDefaultAsync(l => l.UserId == request.UserId, ct);
        if (limit is not null && request.Version != limit.Version)
        {
            throw new DbUpdateConcurrencyException($"The discount limit of {request.UserId} changed since it was read.");
        }

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        var asked = new DiscountAuthority(request.MaxPercent, request.MaxAmount);
        var before = limit is not null ? new DiscountAuthority(limit.MaxPercent, limit.MaxAmount) : await DefaultAuthorityAsync(ct);
        if (DiscountPolicy.Raises(before, asked))
        {
            return await AskLimitAsync(request.UserId, limit, before, asked, asked, notes, ct);
        }

        if (limit is null)
        {
            limit = new DiscountLimit { UserId = request.UserId };
            db.Add(limit);
        }

        limit.MaxPercent = request.MaxPercent;
        limit.MaxAmount = request.MaxAmount;
        limit.Notes = notes;
        await db.SaveChangesAsync(ct);
        return new DiscountLimitOutcome(null);
    }

    /// <summary>
    /// Removes a user's own limit, so the facility default applies to them. Made at once unless the default lets them give
    /// more, which waits as raising the limit does.
    /// </summary>
    public async Task<Result<DiscountLimitOutcome>> DeleteLimitAsync(Guid id, CancellationToken ct)
    {
        var limit = await db.Set<DiscountLimit>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (limit is null)
        {
            return Error.NotFound(nameof(DiscountLimit), id);
        }

        var before = new DiscountAuthority(limit.MaxPercent, limit.MaxAmount);
        var fallback = await DefaultAuthorityAsync(ct);
        if (DiscountPolicy.Raises(before, fallback))
        {
            return await AskLimitAsync(limit.UserId, limit, before, null, fallback, limit.Notes, ct);
        }

        db.Remove(limit);
        await db.SaveChangesAsync(ct);
        return new DiscountLimitOutcome(null);
    }

    private async Task<Result<DiscountLimitOutcome>> AskLimitAsync(
        Guid userId, DiscountLimit? row, DiscountAuthority before, DiscountAuthority? asked, DiscountAuthority after, string? notes, CancellationToken ct)
    {
        if (currentUser.UserId == userId)
        {
            return Error.Forbidden(OwnLimitCode,
                "You cannot raise your own discount limit. Another approver asks for it, and someone else again approves it.");
        }

        var user = await db.Set<User>().Where(u => u.Id == userId).Select(u => new { u.DisplayName, u.UserName }).FirstAsync(ct);
        var replaced = row is null ? null : new DiscountAuthority(row.MaxPercent, row.MaxAmount);
        List<ApprovalDetail> details =
        [
            new("User", $"{user.DisplayName} ({user.UserName})"),
            new("Own-authority discount limit", $"{(row is null ? $"the default ({DiscountPolicy.Words(before)})" : DiscountPolicy.Words(before))} → {(asked is null ? $"the default ({DiscountPolicy.Words(after)})" : DiscountPolicy.Words(after))}"),
            new("What it reaches", "The discount this user may give on any bill without anyone's approval."),
        ];
        var summary = $"Discount limit of {user.DisplayName}: {DiscountPolicy.Words(before)} → {DiscountPolicy.Words(after)}";
        var facility = currentUser.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        var requested = await approvals.RequestAsync(
            LimitKind,
            new ApprovalSubject(nameof(DiscountLimit), userId, row?.Version, facility, null),
            new DiscountLimitPayload(userId, replaced, before, asked, after, notes),
            Fit(summary, 300),
            details,
            notes ?? NoReason,
            ct);
        return requested.IsSuccess ? new DiscountLimitOutcome(requested.Value) : requested.Error!;
    }

    /// <summary>
    /// Makes an approved limit, in the approval's transaction, if the user's own limit is still the one the request
    /// replaces (<c>approval.stale</c>), and never by the user whose limit it is.
    /// </summary>
    internal async Task<Result<string>> LimitApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<DiscountLimitPayload>(request.Payload);
        if (currentUser.UserId == asked.UserId)
        {
            return Error.Forbidden(OwnLimitCode, "This is your own discount limit, so someone else approves it.");
        }

        var name = await db.Set<User>().Where(u => u.Id == asked.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "The user";
        var limit = await db.Set<DiscountLimit>().FirstOrDefaultAsync(l => l.UserId == asked.UserId, ct);
        var now = limit is null ? null : new DiscountAuthority(limit.MaxPercent, limit.MaxAmount);
        if (now != asked.Replaced)
        {
            return Error.Rule("approval.stale",
                $"{name}'s discount limit changed after this was asked for, so nothing was changed. Ask again, for the limit as it is now.");
        }

        if (asked.Asked is { } set)
        {
            if (limit is null)
            {
                limit = new DiscountLimit { UserId = asked.UserId };
                db.Add(limit);
            }

            limit.MaxPercent = set.MaxPercent;
            limit.MaxAmount = set.MaxAmount;
            limit.Notes = asked.Notes;
        }
        else if (limit is not null)
        {
            db.Remove(limit);
        }

        var words = $"{DiscountPolicy.Words(asked.Before)} → {DiscountPolicy.Words(asked.After)}";
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.discount_limit.raised",
            Fit($"Discount limit of {name} raised: {words} (asked for by {request.RequestedByName}, approved by {currentUser.DisplayName ?? currentUser.UserName})", 1000))
        {
            EntityType = nameof(DiscountLimit),
            EntityId = asked.UserId.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { asked.UserId, before = asked.Before, after = asked.After, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        return $"{name}'s limit is now {DiscountPolicy.Words(asked.After)}";
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

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

/// <summary>
/// A discount limit raised, as asked: the user, their own limit it replaces (null: they had none), the limit in force
/// before and after, what is set (null: their own limit removed, so the default applies) and the note.
/// </summary>
public sealed record DiscountLimitPayload(
    Guid UserId, DiscountAuthority? Replaced, DiscountAuthority Before, DiscountAuthority? Asked, DiscountAuthority After, string? Notes);

/// <summary>A discount limit saved or removed: at once (<paramref name="Asked"/> null), or waiting for a second approver.</summary>
public sealed record DiscountLimitOutcome(ApprovalRequestDto? Asked);

/// <summary>
/// A user's own-authority discount limit raised: one approver asks, another approves (<c>billing.discounts.approve</c>),
/// never the one who asked and never the user whose limit it is. Lowering a limit is made at once.
/// </summary>
internal sealed class DiscountLimitApproval(DiscountService discounts) : IApprovalAction
{
    public string Kind => DiscountService.LimitKind;

    public string Title => "Discount limit raised";

    public string ApprovePermission => BillingPermissions.DiscountsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => discounts.LimitApprovedAsync(request, ct);
}
