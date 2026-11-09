using System.Globalization;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.VendorAccounts;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>The debit note or payable adjustment a second person is asked to post.</summary>
public sealed record VendorDocumentPayload(Guid DocumentId);

/// <summary>
/// Purchase returns (debit notes) and supplier payable adjustments (MAKER-CHECKER slice 13). Each is raised by one person and
/// posted only when someone else approves it: a debit note by someone who checks goods receipts against the supplier's
/// invoice (<see cref="SupplyChainPermissions.GrnApprove"/>), when the stock leaves the store; a payable adjustment by someone
/// who approves purchases (<see cref="SupplyChainPermissions.PurchaseApprove"/>). Posted documents reach the Tally day book
/// through <see cref="IVendorAccountsSource"/>: a Debit Note voucher and a Journal.
/// </summary>
public sealed class VendorAccountService(
    HospitalDbContext db, INumberSeries numbers, StockService stock, ICurrentUser currentUser, IApprovals approvals, IAuditLogger audit, TimeProvider time)
{
    public const string ReferenceModule = "supplychain.debit_note";
    public const string DebitNoteKind = "purchase.debit_note";
    public const string AdjustmentKind = "purchase.payable_adjustment";

    // ------------------------------------------------------------------ debit notes

    /// <summary>Raises a debit note and asks a second person to post it, in one transaction.</summary>
    public async Task<Result<ApprovalRequestDto>> RaiseDebitNoteAsync(RaiseDebitNoteRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var vendor = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == request.VendorId, ct);
        if (vendor is null)
        {
            return Error.Field("vendorId", "Choose a supplier.");
        }

        if (!await db.Set<Store>().AnyAsync(s => s.Id == request.StoreId, ct))
        {
            return Error.Field("storeId", "Choose the store the goods go back from.");
        }

        if (request.GoodsReceiptId is { } grnId
            && !await db.Set<GoodsReceiptNote>().AnyAsync(g => g.Id == grnId && g.VendorId == vendor.Id && g.Status == GoodsReceiptStatus.Posted, ct))
        {
            return Error.Field("goodsReceiptId", $"Choose a posted goods receipt from {vendor.Name}.");
        }

        var batchIds = request.Lines.Select(l => l.BatchId).ToList();
        var batches = await db.Set<StockBatch>().AsNoTracking().Where(b => batchIds.Contains(b.Id) && b.StoreId == request.StoreId).ToDictionaryAsync(b => b.Id, ct);
        var itemIds = batches.Values.Select(b => b.ItemId).Distinct().ToList();
        var items = await db.Set<Item>().AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        var facilityGstin = await db.Set<Facility>().Where(f => f.Id == facilityId).Select(f => f.Gstin).FirstOrDefaultAsync(ct);
        var note = new VendorDebitNote
        {
            FacilityId = facilityId,
            VendorId = vendor.Id,
            StoreId = request.StoreId,
            GoodsReceiptId = request.GoodsReceiptId,
            VendorReference = request.VendorReference?.Trim(),
            Reason = request.Reason.Trim(),
            IsInterState = Vendor.IsInterState(vendor.Gstin, facilityGstin),
            RaisedAt = time.GetUtcNow(),
            RaisedByUserId = currentUser.UserId,
        };
        foreach (var line in request.Lines)
        {
            if (!batches.TryGetValue(line.BatchId, out var batch))
            {
                return Error.Field("lines", "A batch is not in the chosen store.");
            }

            var name = items.GetValueOrDefault(batch.ItemId) ?? string.Empty;
            if (batch.VendorId is { } supplier && supplier != vendor.Id)
            {
                return Error.Rule("debit_note.other_supplier", $"{name} batch {batch.BatchNumber} was bought from another supplier: return it to them.");
            }

            if (line.Quantity > batch.QuantityAvailable)
            {
                return StockErrors.Insufficient($"{name} batch {batch.BatchNumber}", batch.QuantityAvailable, line.Quantity);
            }

            note.AddLine(batch, name, line.Quantity);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        note.Number = await numbers.NextAsync(SupplyChainNumberSeries.DebitNote, facilityId, ct);
        db.Add(note);
        await db.SaveChangesAsync(ct);
        var asked = await AskDebitNoteCoreAsync(note, vendor.Name, ct);
        if (asked.IsFailure)
        {
            return asked.Error!;
        }

        await transaction.CommitAsync(ct);
        return asked.Value;
    }

    /// <summary>Asks again for a debit note whose request was rejected or ran out.</summary>
    public async Task<Result<ApprovalRequestDto>> AskDebitNoteAsync(Guid id, CancellationToken ct)
    {
        var note = await db.Set<VendorDebitNote>().Include(n => n.Lines).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
        {
            return Error.NotFound(nameof(VendorDebitNote), id);
        }

        if (note.Status != VendorDocumentStatus.Draft)
        {
            return Error.Rule("debit_note.not_draft", $"Debit note {note.Number} is {note.Status}.");
        }

        return await AskDebitNoteCoreAsync(note, await VendorNameAsync(note.VendorId, ct), ct);
    }

    public async Task<Result> CancelDebitNoteAsync(Guid id, string reason, CancellationToken ct)
    {
        var note = await db.Set<VendorDebitNote>().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
        {
            return Error.NotFound(nameof(VendorDebitNote), id);
        }

        if ((await approvals.WaitingSinceAsync(DebitNoteKind, nameof(VendorDebitNote), [id], ct)).Count > 0)
        {
            return Error.Conflict("debit_note.waiting", $"Debit note {note.Number} is waiting for approval. Withdraw the request in Approvals first.");
        }

        var cancelled = note.Cancel(reason.Trim(), time.GetUtcNow());
        if (cancelled.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return cancelled;
    }

    /// <summary>
    /// Posts an approved debit note, in the approval's transaction: as raised, or not at all. The stock leaves the batches
    /// as a purchase return, which needs it still there and not set aside.
    /// </summary>
    internal async Task<Result<string>> PostDebitNoteAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<VendorDocumentPayload>(request.Payload);
        var note = await db.Set<VendorDebitNote>().Include(n => n.Lines).FirstOrDefaultAsync(n => n.Id == asked.DocumentId, ct);
        if (note is null)
        {
            return Error.NotFound(nameof(VendorDebitNote), asked.DocumentId);
        }

        if (note.Status != VendorDocumentStatus.Draft || note.Version != request.SubjectVersion)
        {
            return Error.Rule("approval.stale", $"Debit note {note.Number} changed after it was sent for approval (it is {note.Status} now), so nothing was posted.");
        }

        var posted = note.MarkPosted(currentUser.UserId, time.GetUtcNow());
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var vendor = await VendorNameAsync(note.VendorId, ct);
        var reference = new StockReference(ReferenceModule, note.Id, note.Number, Fit($"Returned to {vendor}: {note.Reason}", 200));
        foreach (var line in note.Lines)
        {
            var moved = await stock.AdjustBatchAsync(line.StockBatchId, -line.Quantity, StockMovementType.PurchaseReturn, reference, ct);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }
        }

        var approver = currentUser.DisplayName ?? currentUser.UserName;
        audit.Add(new AuditEntry(AuditCategories.Admin, "purchase.debit_note_posted",
            Fit($"Debit note {note.Number} to {vendor} posted: {note.Lines.Count} line(s), {DocumentFormat.Money(note.TotalAmount)}; raised by {request.RequestedByName}, approved by {approver}", 1000))
        {
            EntityType = nameof(VendorDebitNote),
            EntityId = note.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, raisedBy = request.RequestedBy, note.TotalAmount },
        });
        return $"Debit note {note.Number} posted: stock returned to {vendor}, {DocumentFormat.Money(note.TotalAmount)} off what is owed";
    }

    private async Task<Result<ApprovalRequestDto>> AskDebitNoteCoreAsync(VendorDebitNote note, string vendor, CancellationToken ct)
    {
        var store = await db.Set<Store>().AsNoTracking().Where(s => s.Id == note.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var gst = note.CgstAmount + note.SgstAmount + note.IgstAmount;
        List<ApprovalDetail> details =
        [
            new("Debit note", note.Number),
            new("Supplier", vendor),
            new("Supplier's reference", note.VendorReference ?? "None"),
            new("From store", store),
            .. note.Lines.OrderBy(l => l.ItemName).Select(l => new ApprovalDetail(
                $"{l.ItemName} batch {l.BatchNumber}", $"{Qty(l.Quantity)} at {DocumentFormat.Money(l.UnitCost)}: {DocumentFormat.Money(l.TaxableAmount)}")),
            new("Value at cost", DocumentFormat.Money(note.TaxableAmount)),
            new("Input GST taken back", DocumentFormat.Money(gst)),
            new("What it changes",
                $"The stock leaves {store} as a purchase return, and {DocumentFormat.Money(note.TotalAmount)} comes off what the hospital owes {vendor} in Tally."),
        ];
        return await approvals.RequestAsync(
            DebitNoteKind,
            new ApprovalSubject(nameof(VendorDebitNote), note.Id, note.Version, note.FacilityId, null),
            new VendorDocumentPayload(note.Id),
            Fit($"Debit note {note.Number}: {vendor}, {note.Lines.Count} line(s), {DocumentFormat.Money(note.TotalAmount)}", 300),
            details,
            note.Reason,
            ct);
    }

    // ------------------------------------------------------------------ payable adjustments

    public async Task<Result<ApprovalRequestDto>> RaiseAdjustmentAsync(RaisePayableAdjustmentRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var vendor = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == request.VendorId, ct);
        if (vendor is null)
        {
            return Error.Field("vendorId", "Choose a supplier.");
        }

        var adjustment = new VendorPayableAdjustment
        {
            FacilityId = facilityId,
            VendorId = vendor.Id,
            Direction = request.Direction,
            Amount = Money.Round(request.Amount),
            Reason = request.Reason.Trim(),
            VendorReference = request.VendorReference?.Trim(),
            RaisedAt = time.GetUtcNow(),
            RaisedByUserId = currentUser.UserId,
        };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        adjustment.Number = await numbers.NextAsync(SupplyChainNumberSeries.PayableAdjustment, facilityId, ct);
        db.Add(adjustment);
        await db.SaveChangesAsync(ct);
        var asked = await AskAdjustmentCoreAsync(adjustment, vendor.Name, ct);
        if (asked.IsFailure)
        {
            return asked.Error!;
        }

        await transaction.CommitAsync(ct);
        return asked.Value;
    }

    public async Task<Result<ApprovalRequestDto>> AskAdjustmentAsync(Guid id, CancellationToken ct)
    {
        var adjustment = await db.Set<VendorPayableAdjustment>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (adjustment is null)
        {
            return Error.NotFound(nameof(VendorPayableAdjustment), id);
        }

        if (adjustment.Status != VendorDocumentStatus.Draft)
        {
            return Error.Rule("payable_adjustment.not_draft", $"Payable adjustment {adjustment.Number} is {adjustment.Status}.");
        }

        return await AskAdjustmentCoreAsync(adjustment, await VendorNameAsync(adjustment.VendorId, ct), ct);
    }

    public async Task<Result> CancelAdjustmentAsync(Guid id, string reason, CancellationToken ct)
    {
        var adjustment = await db.Set<VendorPayableAdjustment>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (adjustment is null)
        {
            return Error.NotFound(nameof(VendorPayableAdjustment), id);
        }

        if ((await approvals.WaitingSinceAsync(AdjustmentKind, nameof(VendorPayableAdjustment), [id], ct)).Count > 0)
        {
            return Error.Conflict("payable_adjustment.waiting", $"Payable adjustment {adjustment.Number} is waiting for approval. Withdraw the request in Approvals first.");
        }

        var cancelled = adjustment.Cancel(reason.Trim(), time.GetUtcNow());
        if (cancelled.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return cancelled;
    }

    internal async Task<Result<string>> PostAdjustmentAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<VendorDocumentPayload>(request.Payload);
        var adjustment = await db.Set<VendorPayableAdjustment>().FirstOrDefaultAsync(a => a.Id == asked.DocumentId, ct);
        if (adjustment is null)
        {
            return Error.NotFound(nameof(VendorPayableAdjustment), asked.DocumentId);
        }

        if (adjustment.Status != VendorDocumentStatus.Draft || adjustment.Version != request.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"Payable adjustment {adjustment.Number} changed after it was sent for approval (it is {adjustment.Status} now), so nothing was posted.");
        }

        var posted = adjustment.MarkPosted(currentUser.UserId, time.GetUtcNow());
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var vendor = await VendorNameAsync(adjustment.VendorId, ct);
        var words = AdjustmentWords(adjustment, vendor);
        audit.Add(new AuditEntry(AuditCategories.Admin, "purchase.payable_adjustment_posted",
            Fit($"Payable adjustment {adjustment.Number} posted: {words}; raised by {request.RequestedByName}, approved by {currentUser.DisplayName ?? currentUser.UserName}", 1000))
        {
            EntityType = nameof(VendorPayableAdjustment),
            EntityId = adjustment.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, raisedBy = request.RequestedBy, adjustment.Direction, adjustment.Amount },
        });
        return $"Payable adjustment {adjustment.Number} posted: {words}";
    }

    private Task<Result<ApprovalRequestDto>> AskAdjustmentCoreAsync(VendorPayableAdjustment adjustment, string vendor, CancellationToken ct)
    {
        var words = AdjustmentWords(adjustment, vendor);
        List<ApprovalDetail> details =
        [
            new("Adjustment", adjustment.Number),
            new("Supplier", vendor),
            new("Supplier's reference", adjustment.VendorReference ?? "None"),
            new("Amount", DocumentFormat.Money(adjustment.Amount)),
            new("What it changes", $"In Tally, {words}."),
        ];
        return approvals.RequestAsync(
            AdjustmentKind,
            new ApprovalSubject(nameof(VendorPayableAdjustment), adjustment.Id, adjustment.Version, adjustment.FacilityId, null),
            new VendorDocumentPayload(adjustment.Id),
            Fit($"Payable adjustment {adjustment.Number}: {words}", 300),
            details,
            adjustment.Reason,
            ct);
    }

    internal static string AdjustmentWords(VendorPayableAdjustment adjustment, string vendor) =>
        adjustment.Direction == PayableAdjustmentDirection.Reduce
            ? $"the hospital owes {vendor} {DocumentFormat.Money(adjustment.Amount)} less"
            : $"the hospital owes {vendor} {DocumentFormat.Money(adjustment.Amount)} more";

    private async Task<string> VendorNameAsync(Guid vendorId, CancellationToken ct) =>
        await db.Set<Vendor>().AsNoTracking().Where(v => v.Id == vendorId).Select(v => v.Name).FirstOrDefaultAsync(ct) ?? string.Empty;

    private static string Qty(decimal quantity) => quantity.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A purchase return posted: the store raises it, someone else who checks goods receipts approves it.</summary>
internal sealed class DebitNoteApproval(VendorAccountService accounts) : IApprovalAction
{
    public string Kind => VendorAccountService.DebitNoteKind;

    public string Title => "Purchase return (debit note)";

    public string ApprovePermission => SupplyChainPermissions.GrnApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => accounts.PostDebitNoteAsync(request, ct);
}

/// <summary>A supplier payable adjustment posted: purchase raises it, someone else who approves purchases decides.</summary>
internal sealed class PayableAdjustmentApproval(VendorAccountService accounts) : IApprovalAction
{
    public string Kind => VendorAccountService.AdjustmentKind;

    public string Title => "Supplier payable adjustment";

    public string ApprovePermission => SupplyChainPermissions.PurchaseApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => accounts.PostAdjustmentAsync(request, ct);
}

/// <summary>Posted debit notes and payable adjustments of a day, for Billing's Tally day book (<see cref="IVendorAccountsSource"/>).</summary>
internal sealed class VendorAccountsDayBookSource(HospitalDbContext db) : IVendorAccountsSource
{
    public async Task<VendorAccountsDay> GetDayAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken)
    {
        var (start, end) = HospitalClock.Range(date, date);
        var vendors = db.Set<Vendor>().AsNoTracking();
        var notes = await db.Set<VendorDebitNote>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(n => n.FacilityId == facilityId && n.Status == VendorDocumentStatus.Posted && n.PostedAt >= start && n.PostedAt < end)
            .OrderBy(n => n.PostedAt)
            .Select(n => new VendorDebitNoteEntry(
                n.Number, vendors.Where(v => v.Id == n.VendorId).Select(v => v.Name).FirstOrDefault() ?? string.Empty, n.VendorReference,
                n.TaxableAmount, n.CgstAmount, n.SgstAmount, n.IgstAmount))
            .ToListAsync(cancellationToken);
        var adjustments = await db.Set<VendorPayableAdjustment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => a.FacilityId == facilityId && a.Status == VendorDocumentStatus.Posted && a.PostedAt >= start && a.PostedAt < end)
            .OrderBy(a => a.PostedAt)
            .Select(a => new VendorPayableAdjustmentEntry(
                a.Number, vendors.Where(v => v.Id == a.VendorId).Select(v => v.Name).FirstOrDefault() ?? string.Empty,
                a.Direction == PayableAdjustmentDirection.Reduce, a.Amount, a.VendorReference))
            .ToListAsync(cancellationToken);
        return new VendorAccountsDay(notes, adjustments);
    }
}
