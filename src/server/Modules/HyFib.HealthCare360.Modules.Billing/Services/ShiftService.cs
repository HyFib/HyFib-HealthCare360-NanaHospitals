using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Cashier shifts and day-end closing (OPB-07). Counter money (receipts and cash refunds) is tied to the collector's
/// open shift; closing compares the declared drawer with the expected cash and audits any variance. A shortage beyond
/// the hospital's tolerance is not settled on the cashier's word: the shift closes "shortage awaiting supervisor" and a
/// supervisor, never the cashier, accepts it (<see cref="ShortageKind"/>). An excess is recorded and flagged, and needs
/// no one's approval: no money is missing.
/// </summary>
public sealed class ShiftService(
    HospitalDbContext db,
    ISettingsService settings,
    IPermissionService permissions,
    IApprovals approvals,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>When true (default), money can be collected or paid out only in an open shift.</summary>
    public const string RequiredSetting = "billing.shift.required";

    /// <summary>A shift closed short, waiting for a supervisor to accept the shortage.</summary>
    public const string ShortageKind = "billing.shift_shortage";

    /// <summary>
    /// How much (₹) a drawer may be short and still close on the cashier's word; beyond it a supervisor accepts the
    /// shortage. Raising it waits for a second administrator (SettingGuard).
    /// </summary>
    public const string ShortageToleranceSetting = "billing.shift.shortageTolerance";

    /// <summary>The tolerance while none is set: every shortage waits for a supervisor.</summary>
    public const decimal DefaultShortageTolerance = 0m;

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

    public async Task<Result<ShiftCloseOutcome>> CloseAsync(Guid id, CloseShiftRequest request, CancellationToken ct)
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
        var tolerance = await ShortageToleranceAsync(shift.FacilityId, ct);
        var awaiting = ShiftCalculator.ShortageNeedsSupervisor(reconciliation.Variance, tolerance);
        if (awaiting && string.IsNullOrWhiteSpace(request.Notes))
        {
            return Error.Field("notes",
                $"The drawer is ₹{Money.FormatInr(-reconciliation.Variance)} short. Say why: a supervisor accepts the shortage with your reason.");
        }

        var closed = shift.Close(reconciliation, request.Notes, time.GetUtcNow(), tolerance);
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        if (reconciliation.Variance != 0)
        {
            var kind = reconciliation.Variance < 0 ? "shortage" : "excess";
            audit.Add(new AuditEntry(AuditCategories.Data, "billing.shift.variance",
                $"Cash {kind} of ₹{Money.FormatInr(Math.Abs(reconciliation.Variance))} at {shift.Counter} ({shift.UserName}){(awaiting ? ", awaiting a supervisor" : string.Empty)}")
            {
                EntityType = nameof(CashierShift),
                EntityId = shift.Id.ToString(),
                Severity = AuditSeverity.Warning,
                Details = new { reconciliation.ExpectedCash, reconciliation.DeclaredCash, reconciliation.Variance, awaiting },
            });
        }

        // The shift closes, and its shortage is asked for, together or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        ApprovalRequestDto? asked = null;
        if (awaiting)
        {
            var requested = await AskShortageCoreAsync(shift, shift.Notes!, ct);
            if (requested.IsFailure)
            {
                return requested.Error!;
            }

            asked = requested.Value;
        }

        await transaction.CommitAsync(ct);
        var dto = ToDto(shift, await db.Set<Receipt>().CountAsync(r => r.ShiftId == shift.Id, ct)) with { ShortageApprovalId = asked?.Id };
        return new ShiftCloseOutcome(dto, asked);
    }

    /// <summary>
    /// Asks again for a supervisor to accept a shift's shortage, when the last request was rejected, withdrawn or expired:
    /// the shortage stays the shift's, awaiting, until one is accepted.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> AskShortageAsync(Guid id, string reason, CancellationToken ct)
    {
        var shift = await db.Set<CashierShift>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (shift is null)
        {
            return Error.NotFound(nameof(CashierShift), id);
        }

        if (!shift.ShortageAwaiting)
        {
            return Error.Rule("billing.shift_shortage_not_awaiting", "This shift's shortage is not waiting for a supervisor.");
        }

        return await AskShortageCoreAsync(shift, reason, ct);
    }

    private async Task<Result<ApprovalRequestDto>> AskShortageCoreAsync(CashierShift shift, string reason, CancellationToken ct)
    {
        List<ApprovalDetail> details =
        [
            new("Shift", $"{shift.Counter}, {shift.UserName}: opened {DocumentFormatShort(shift.OpenedAt)}, closed {DocumentFormatShort(shift.ClosedAt)}"),
            new("Cash expected", $"₹{Money.FormatInr(shift.ExpectedCash)} (opening float ₹{Money.FormatInr(shift.OpeningFloat)} and the cash taken, less cash paid out)"),
            new("Cash declared", $"₹{Money.FormatInr(shift.DeclaredCash ?? 0m)}"),
            new("Shortage", $"₹{Money.FormatInr(shift.Shortage)}: accepted, it is recorded as the shift's shortage, accepted by you"),
        ];
        return await approvals.RequestAsync(
            ShortageKind,
            new ApprovalSubject(nameof(CashierShift), shift.Id, shift.Version, shift.FacilityId, null),
            new ShiftShortagePayload(shift.Id, shift.Variance ?? 0m),
            $"Cash short by ₹{Money.FormatInr(shift.Shortage)} at {shift.Counter} ({shift.UserName})",
            details,
            reason,
            ct);
    }

    /// <summary>
    /// Accepts an approved shortage, in the approval's transaction: only the shortage as it was asked about
    /// (<c>approval.stale</c>), and never by the cashier whose shift it is.
    /// </summary>
    internal async Task<Result<string>> ShortageApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ShiftShortagePayload>(request.Payload);
        var shift = await db.Set<CashierShift>().FirstOrDefaultAsync(s => s.Id == asked.ShiftId, ct);
        if (shift is null)
        {
            return Error.NotFound(nameof(CashierShift), asked.ShiftId);
        }

        if (currentUser.UserId is not { } supervisor)
        {
            return BillingDb.SignInRequired;
        }

        if (shift.ShortageAwaiting && shift.Variance != asked.Variance)
        {
            return Error.Rule("approval.stale", "The shift's count changed after this was asked for, so nothing was accepted. Ask again.");
        }

        var name = currentUser.DisplayName ?? currentUser.UserName ?? "Supervisor";
        var accepted = shift.AcceptShortage(supervisor, name, time.GetUtcNow());
        if (accepted.IsFailure)
        {
            return accepted.Error!.Type == ErrorType.Forbidden
                ? accepted.Error
                : Error.Rule("approval.stale", $"{accepted.Error.Message} Nothing was changed.");
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "billing.shift.shortage_accepted",
            $"Cash shortage of ₹{Money.FormatInr(shift.Shortage)} at {shift.Counter} ({shift.UserName}) accepted by {name}, asked for by {request.RequestedByName}: {request.Reason}")
        {
            EntityType = nameof(CashierShift),
            EntityId = shift.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { shift.Variance, cashier = shift.UserId, approval = request.Id, requestedBy = request.RequestedBy, acceptedBy = supervisor },
        });
        return $"Shortage of ₹{Money.FormatInr(shift.Shortage)} accepted";
    }

    /// <summary>The facility's shortage tolerance (<see cref="ShortageToleranceSetting"/>).</summary>
    public async Task<decimal> ShortageToleranceAsync(Guid facilityId, CancellationToken ct) =>
        Math.Max(await settings.GetAsync<decimal?>(ShortageToleranceSetting, facilityId, ct) ?? DefaultShortageTolerance, 0m);

    /// <summary>The shortage request waiting for each of these shifts.</summary>
    public async Task<IReadOnlyDictionary<Guid, Guid>> ShortageRequestsAsync(IReadOnlyCollection<Guid> shiftIds, CancellationToken ct)
    {
        if (shiftIds.Count == 0)
        {
            return new Dictionary<Guid, Guid>();
        }

        var now = time.GetUtcNow();
        return await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == ShortageKind && r.SubjectType == nameof(CashierShift) && shiftIds.Contains(r.SubjectId) && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .GroupBy(r => r.SubjectId)
            .Select(g => new { g.Key, Id = g.OrderBy(r => r.RequestedAt).Select(r => r.Id).First() })
            .ToDictionaryAsync(r => r.Key, r => r.Id, ct);
    }

    private static string DocumentFormatShort(DateTimeOffset? instant) => HyFib.HealthCare360.Platform.Documents.DocumentFormat.DateTime(instant);

    public async Task<ShiftDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var shift = await db.Set<CashierShift>().AsNoTracking().Include(s => s.Totals).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (shift is null)
        {
            return null;
        }

        var dto = await WithLiveTotalsAsync(shift, ct);
        return shift.ShortageAwaiting && (await ShortageRequestsAsync([shift.Id], ct)).TryGetValue(shift.Id, out var request)
            ? dto with { ShortageApprovalId = request }
            : dto;
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
            ShortageTolerance = await ShortageToleranceAsync(shift.FacilityId, ct),
            Totals = live.Totals.Select(t => new ShiftModeTotalDto(t.Mode, t.Collected, t.Refunded, t.Net)).ToList(),
        };
    }

    public Task<DayEndReport> DayEndAsync(DateOnly date, string? counter, CancellationToken ct) => DayEndAsync(db.CurrentFacilityId, date, counter, ct);

    /// <summary>
    /// The day-end of one facility (null: every facility). Background work has no signed-in facility, so the Tally
    /// reconciliation names it and gets the same figures as the cashier's report.
    /// </summary>
    public async Task<DayEndReport> DayEndAsync(Guid? facilityId, DateOnly date, string? counter, CancellationToken ct)
    {
        var (start, end) = BillingTime.Range(date, date);
        var counterName = string.IsNullOrWhiteSpace(counter) ? null : counter.Trim();

        var shiftQuery = Of<CashierShift>(facilityId).Where(s => s.OpenedAt < end && (s.ClosedAt == null || s.ClosedAt >= start));
        if (counterName is not null)
        {
            shiftQuery = shiftQuery.Where(s => s.Counter == counterName);
        }

        var shifts = await shiftQuery.OrderBy(s => s.OpenedAt).ToListAsync(ct);

        var receiptQuery = Of<Receipt>(facilityId)
            .Where(r => r.ReceivedAt >= start && r.ReceivedAt < end && r.Purpose != ReceiptPurpose.PayerSettlement);
        if (counterName is not null)
        {
            receiptQuery = receiptQuery.Where(r => r.Counter == counterName);
        }

        var payments = await receiptQuery
            .SelectMany(r => r.Payments, (r, p) => new { r.ShiftId, r.Purpose, p.Mode, p.Amount })
            .ToListAsync(ct);

        var refundQuery = Of<Refund>(facilityId)
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

        var billed = await Of<Bill>(facilityId)
            .Where(b => b.FinalizedAt >= start && b.FinalizedAt < end)
            .Select(b => new { b.NetAmount, b.DiscountAmount })
            .ToListAsync(ct);
        var cancelled = await Of<Bill>(facilityId)
            .Where(b => b.CancelledAt >= start && b.CancelledAt < end)
            .Select(b => b.NetAmount)
            .ToListAsync(ct);

        // Reversals are made by the accounts office, so only the whole day's report has them. Undoing an advance
        // adjustment moved no money, as the adjustment did not.
        var notReceived = counterName is not null
            ? []
            : await Of<ReceiptReversal>(facilityId)
                .Where(r => r.ReversedAt >= start && r.ReversedAt < end)
                .SelectMany(r => r.Lines, (r, l) => new { r.Id, l.Mode, l.Amount })
                .Where(l => l.Mode != PaymentMode.Advance)
                .GroupBy(l => l.Mode)
                .Select(g => new DayEndReversalTotal(g.Key, g.Sum(l => l.Amount), g.Select(l => l.Id).Distinct().Count()))
                .ToListAsync(ct);
        var notPaidOut = counterName is not null
            ? []
            : await Of<RefundReversal>(facilityId)
                .Where(r => r.ReversedAt >= start && r.ReversedAt < end)
                .GroupBy(r => r.Mode)
                .Select(g => new DayEndReversalTotal(g.Key, g.Sum(r => r.Amount), g.Count()))
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
                expected, s.DeclaredCash, s.Variance, s.ShortageAwaiting, s.ShortageAcceptedByName);
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
            shifts.Count(s => s.Status == ShiftStatus.Open),
            notReceived.OrderBy(t => t.Mode).ToList(),
            notPaidOut.OrderBy(t => t.Mode).ToList(),
            byMode.Sum(t => t.Net) - notReceived.Sum(t => t.Amount) + notPaidOut.Sum(t => t.Amount),
            shifts.Count(s => s.ShortageAwaiting));
    }

    /// <summary>The facility's rows, read-only; a null facility reads every facility's, as the facility filter does without one.</summary>
    private IQueryable<T> Of<T>(Guid? facilityId)
        where T : class, IFacilityScoped
    {
        var rows = db.Set<T>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking();
        return facilityId is { } id ? rows.Where(x => x.FacilityId == id) : rows;
    }

    internal static ShiftDto ToDto(CashierShift s, int receiptCount) => new(
        s.Id, s.UserId, s.UserName, s.Counter, s.Status, s.OpenedAt, s.ClosedAt, s.OpeningFloat, s.ExpectedCash, s.DeclaredCash, s.Variance,
        s.Totals.OrderBy(t => t.Mode).Select(t => new ShiftModeTotalDto(t.Mode, t.Collected, t.Refunded, t.Net)).ToList(), receiptCount, s.Notes, s.Version,
        s.ShortageAwaiting, s.ShortageAcceptedByName, s.ShortageAcceptedAt);

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

/// <summary>A shift's shortage as asked about: the variance it closed with (a recount changed meanwhile is not accepted).</summary>
public sealed record ShiftShortagePayload(Guid ShiftId, decimal Variance);

/// <summary>A shift closed: at once, or with its shortage asked of a supervisor (<paramref name="Asked"/>).</summary>
public sealed record ShiftCloseOutcome(ShiftDto Shift, ApprovalRequestDto? Asked);

/// <summary>
/// A cash shortage at a shift's close, beyond the hospital's tolerance: the cashier (or the supervisor who closed it) asks,
/// and someone else who approves refunds accepts it, never the cashier whose shift it is.
/// </summary>
internal sealed class ShiftShortageApproval(ShiftService shifts) : IApprovalAction
{
    public string Kind => ShiftService.ShortageKind;

    public string Title => "Cash shortage at a shift's close";

    public string ApprovePermission => BillingPermissions.RefundsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => shifts.ShortageApprovedAsync(request, ct);
}
