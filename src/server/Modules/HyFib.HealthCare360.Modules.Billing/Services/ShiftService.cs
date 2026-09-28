using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Cashier shifts and day-end closing (OPB-07). Counter money (receipts and cash refunds) is tied to the collector's
/// open shift; closing compares the declared drawer with the expected cash and audits any variance.
/// </summary>
public sealed class ShiftService(
    HospitalDbContext db,
    ISettingsService settings,
    IPermissionService permissions,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>When true (default), money can be collected or paid out only in an open shift.</summary>
    public const string RequiredSetting = "billing.shift.required";

    public async Task<CashierShift?> CurrentAsync(CancellationToken ct) =>
        currentUser.UserId is { } userId
            ? await db.Set<CashierShift>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(s => s.Totals)
                .FirstOrDefaultAsync(s => s.UserId == userId && s.Status == ShiftStatus.Open, ct)
            : null;

    /// <summary>The user's open shift, or an error when shifts are mandatory and none is open.</summary>
    internal async Task<Result<CashierShift?>> RequireOpenShiftAsync(CancellationToken ct)
    {
        var shift = await CurrentAsync(ct);
        if (shift is not null)
        {
            return Result<CashierShift?>.Ok(shift);
        }

        var required = await settings.GetAsync<bool?>(RequiredSetting, currentUser.FacilityId, ct) ?? true;
        if (required)
        {
            return Error.Rule("billing.shift_required", "Open your cash counter shift before collecting or paying out money.");
        }

        return Result<CashierShift?>.Ok(null);
    }

    public async Task<Result<ShiftDto>> OpenAsync(OpenShiftRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired;
        }

        if (currentUser.UserId is not { } userId)
        {
            return BillingDb.SignInRequired;
        }

        if (await CurrentAsync(ct) is not null)
        {
            return Error.Conflict("billing.shift_open", "You already have an open shift. Close it before opening a new one.");
        }

        var shift = CashierShift.Open(facilityId, userId, currentUser.DisplayName ?? currentUser.UserName ?? "Cashier", request.Counter, request.OpeningFloat, time.GetUtcNow());
        db.Add(shift);
        await db.SaveChangesAsync(ct);
        return ToDto(shift, 0);
    }

    public async Task<Result<ShiftDto>> CloseAsync(Guid id, CloseShiftRequest request, CancellationToken ct)
    {
        var shift = await db.Set<CashierShift>().Include(s => s.Totals).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (shift is null)
        {
            return Error.NotFound(nameof(CashierShift), id);
        }

        if (currentUser.UserId is not { } userId)
        {
            return BillingDb.SignInRequired;
        }

        if (shift.UserId != userId && !await permissions.HasPermissionAsync(userId, BillingPermissions.RefundsApprove, ct))
        {
            return Error.Forbidden("billing.shift_not_yours", "Only the cashier who opened this shift, or a supervisor, can close it.");
        }

        db.SetExpectedVersion(shift, request.Version);
        var reconciliation = ShiftCalculator.Reconcile(shift.OpeningFloat, await MovementsAsync(shift.Id, ct), request.DeclaredCash);
        var closed = shift.Close(reconciliation, request.Notes, time.GetUtcNow());
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        if (reconciliation.Variance != 0)
        {
            var kind = reconciliation.Variance < 0 ? "shortage" : "excess";
            audit.Add(new AuditEntry(AuditCategories.Data, "billing.shift.variance",
                $"Cash {kind} of ₹{Money.FormatInr(Math.Abs(reconciliation.Variance))} at {shift.Counter} ({shift.UserName})")
            {
                EntityType = nameof(CashierShift),
                EntityId = shift.Id.ToString(),
                Severity = AuditSeverity.Warning,
                Details = new { reconciliation.ExpectedCash, reconciliation.DeclaredCash, reconciliation.Variance },
            });
        }

        await db.SaveChangesAsync(ct);
        return ToDto(shift, await db.Set<Receipt>().CountAsync(r => r.ShiftId == shift.Id, ct));
    }

    public async Task<ShiftDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var shift = await db.Set<CashierShift>().AsNoTracking().Include(s => s.Totals).FirstOrDefaultAsync(s => s.Id == id, ct);
        return shift is null ? null : await WithLiveTotalsAsync(shift, ct);
    }

    /// <summary>An open shift shows running totals; a closed one its frozen reconciliation.</summary>
    public async Task<ShiftDto> WithLiveTotalsAsync(CashierShift shift, CancellationToken ct)
    {
        var receipts = await db.Set<Receipt>().CountAsync(r => r.ShiftId == shift.Id, ct);
        if (shift.Status == ShiftStatus.Closed)
        {
            return ToDto(shift, receipts);
        }

        var live = ShiftCalculator.Reconcile(shift.OpeningFloat, await MovementsAsync(shift.Id, ct), 0m);
        return ToDto(shift, receipts) with
        {
            ExpectedCash = live.ExpectedCash,
            Totals = live.Totals.Select(t => new ShiftModeTotalDto(t.Mode, t.Collected, t.Refunded, t.Net)).ToList(),
        };
    }

    public async Task<DayEndReport> DayEndAsync(DateOnly date, string? counter, CancellationToken ct)
    {
        var (start, end) = BillingTime.Range(date, date);
        var counterName = string.IsNullOrWhiteSpace(counter) ? null : counter.Trim();

        var shiftQuery = db.Set<CashierShift>().AsNoTracking().Where(s => s.OpenedAt < end && (s.ClosedAt == null || s.ClosedAt >= start));
        if (counterName is not null)
        {
            shiftQuery = shiftQuery.Where(s => s.Counter == counterName);
        }

        var shifts = await shiftQuery.OrderBy(s => s.OpenedAt).ToListAsync(ct);

        var receiptQuery = db.Set<Receipt>().AsNoTracking()
            .Where(r => r.ReceivedAt >= start && r.ReceivedAt < end && r.Purpose != ReceiptPurpose.PayerSettlement);
        if (counterName is not null)
        {
            receiptQuery = receiptQuery.Where(r => r.Counter == counterName);
        }

        var payments = await receiptQuery
            .SelectMany(r => r.Payments, (r, p) => new { r.ShiftId, r.Purpose, p.Mode, p.Amount })
            .ToListAsync(ct);

        var refundQuery = db.Set<Refund>().AsNoTracking()
            .Where(r => r.Status == RefundStatus.Paid && r.PaidAt >= start && r.PaidAt < end && r.Mode != null && r.Mode != PaymentMode.Advance);
        if (counterName is not null)
        {
            refundQuery = refundQuery.Where(r => r.Counter == counterName);
        }

        var refunds = await refundQuery.Select(r => new { r.ShiftId, Mode = r.Mode!.Value, r.Amount }).ToListAsync(ct);

        var byMode = ShiftCalculator.Reconcile(
            0m,
            payments.Select(p => new ShiftMovement(p.Mode, p.Amount, false)).Concat(refunds.Select(r => new ShiftMovement(r.Mode, r.Amount, true))),
            0m).Totals;

        var billed = await db.Set<Bill>().AsNoTracking()
            .Where(b => b.FinalizedAt >= start && b.FinalizedAt < end)
            .Select(b => new { b.NetAmount, b.DiscountAmount })
            .ToListAsync(ct);
        var cancelled = await db.Set<Bill>().AsNoTracking()
            .Where(b => b.CancelledAt >= start && b.CancelledAt < end)
            .Select(b => b.NetAmount)
            .ToListAsync(ct);

        var rows = shifts.Select(s =>
        {
            var inShift = payments.Where(p => p.ShiftId == s.Id && p.Mode is not (PaymentMode.Advance or PaymentMode.Insurance)).ToList();
            var outShift = refunds.Where(r => r.ShiftId == s.Id).ToList();
            var expected = s.Status == ShiftStatus.Closed
                ? s.ExpectedCash
                : s.OpeningFloat + inShift.Where(p => p.Mode == PaymentMode.Cash).Sum(p => p.Amount) - outShift.Where(r => r.Mode == PaymentMode.Cash).Sum(r => r.Amount);
            return new DayEndCashierRow(
                s.Id, s.UserId, s.UserName, s.Counter, s.Status, s.OpenedAt, s.ClosedAt, inShift.Sum(p => p.Amount), outShift.Sum(r => r.Amount),
                expected, s.DeclaredCash, s.Variance);
        }).ToList();

        return new DayEndReport(
            date,
            counterName,
            rows,
            byMode.Select(t => new ShiftModeTotalDto(t.Mode, t.Collected, t.Refunded, t.Net)).ToList(),
            byMode.Sum(t => t.Collected),
            byMode.Sum(t => t.Refunded),
            byMode.Sum(t => t.Net),
            payments.Where(p => p.Purpose is ReceiptPurpose.Deposit or ReceiptPurpose.Advance && p.Mode != PaymentMode.Advance).Sum(p => p.Amount),
            billed.Count,
            billed.Sum(b => b.NetAmount),
            billed.Sum(b => b.DiscountAmount),
            cancelled.Count,
            cancelled.Sum(),
            shifts.Count(s => s.Status == ShiftStatus.Open));
    }

    internal static ShiftDto ToDto(CashierShift s, int receiptCount) => new(
        s.Id, s.UserId, s.UserName, s.Counter, s.Status, s.OpenedAt, s.ClosedAt, s.OpeningFloat, s.ExpectedCash, s.DeclaredCash, s.Variance,
        s.Totals.OrderBy(t => t.Mode).Select(t => new ShiftModeTotalDto(t.Mode, t.Collected, t.Refunded, t.Net)).ToList(), receiptCount, s.Notes, s.Version);

    private async Task<List<ShiftMovement>> MovementsAsync(Guid shiftId, CancellationToken ct)
    {
        var collected = await db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(r => r.ShiftId == shiftId)
            .SelectMany(r => r.Payments)
            .Select(p => new ShiftMovement(p.Mode, p.Amount, false))
            .ToListAsync(ct);
        var paidOut = await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.ShiftId == shiftId && r.Status == RefundStatus.Paid && r.Mode != null)
            .Select(r => new ShiftMovement(r.Mode!.Value, r.Amount, true))
            .ToListAsync(ct);
        return [.. collected, .. paidOut];
    }
}
