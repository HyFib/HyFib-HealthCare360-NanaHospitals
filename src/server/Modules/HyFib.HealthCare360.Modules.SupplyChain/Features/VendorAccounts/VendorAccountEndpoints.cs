using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.VendorAccounts;

/// <summary>
/// /api/v1/purchase/debit-notes and /api/v1/purchase/payable-adjustments — purchase returns and supplier payable adjustments.
/// Raising one answers 202 with the approval it asks for: it posts (and reaches Tally) only when someone else approves it.
/// </summary>
internal static class VendorAccountEndpoints
{
    private static readonly string[] Readers =
        [SupplyChainPermissions.GrnReceive, SupplyChainPermissions.GrnApprove, SupplyChainPermissions.PurchaseManage, SupplyChainPermissions.PurchaseApprove];

    private static readonly string[] DebitNoteRaisers = [SupplyChainPermissions.GrnReceive, SupplyChainPermissions.PurchaseManage];

    public static void Map(IEndpointRouteBuilder app)
    {
        var notes = app.MapGroup("/api/v1/purchase/debit-notes").WithTags("Purchase");
        notes.MapGet("/", ListNotes).RequireAnyPermission(Readers);
        notes.MapGet("/{id:guid}", GetNote).RequireAnyPermission(Readers);
        notes.MapPost("/", async (RaiseDebitNoteRequest r, VendorAccountService accounts, CancellationToken ct) => Accepted(await accounts.RaiseDebitNoteAsync(r, ct)))
            .RequireAnyPermission(DebitNoteRaisers).WithValidation<RaiseDebitNoteRequest>();
        notes.MapPost("/{id:guid}/approval", async (Guid id, VendorAccountService accounts, CancellationToken ct) => Accepted(await accounts.AskDebitNoteAsync(id, ct)))
            .RequireAnyPermission(DebitNoteRaisers);
        notes.MapPost("/{id:guid}/cancel", async (Guid id, InventoryReasonRequest r, VendorAccountService accounts, CancellationToken ct) =>
                (await accounts.CancelDebitNoteAsync(id, r.Reason, ct)).ToNoContent())
            .RequireAnyPermission(DebitNoteRaisers).WithValidation<InventoryReasonRequest>();

        var adjustments = app.MapGroup("/api/v1/purchase/payable-adjustments").WithTags("Purchase");
        adjustments.MapGet("/", ListAdjustments).RequireAnyPermission(Readers);
        adjustments.MapPost("/", async (RaisePayableAdjustmentRequest r, VendorAccountService accounts, CancellationToken ct) => Accepted(await accounts.RaiseAdjustmentAsync(r, ct)))
            .RequirePermission(SupplyChainPermissions.PurchaseManage).WithValidation<RaisePayableAdjustmentRequest>();
        adjustments.MapPost("/{id:guid}/approval", async (Guid id, VendorAccountService accounts, CancellationToken ct) => Accepted(await accounts.AskAdjustmentAsync(id, ct)))
            .RequirePermission(SupplyChainPermissions.PurchaseManage);
        adjustments.MapPost("/{id:guid}/cancel", async (Guid id, InventoryReasonRequest r, VendorAccountService accounts, CancellationToken ct) =>
                (await accounts.CancelAdjustmentAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.PurchaseManage).WithValidation<InventoryReasonRequest>();
    }

    private static IResult Accepted(Result<ApprovalRequestDto> asked) =>
        asked.IsFailure ? asked.Error!.ToProblem() : TypedResults.Accepted($"/api/v1/approvals/{asked.Value.Id}", asked.Value);

    private static async Task<IResult> ListNotes(
        [AsParameters] PageQuery query, VendorDocumentStatus? status, Guid? vendorId, HospitalDbContext db, IApprovals approvals, CancellationToken ct)
    {
        var page = query.ToRequest();
        var notes = db.Set<VendorDebitNote>().AsNoTracking().Include(n => n.Lines).AsQueryable();
        if (status is not null)
        {
            notes = notes.Where(n => n.Status == status);
        }

        if (vendorId is not null)
        {
            notes = notes.Where(n => n.VendorId == vendorId);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            notes = notes.Where(n => n.Number == upper);
        }

        var result = await notes.OrderByDescending(n => n.RaisedAt).ToPagedResultAsync(page, ct);
        var waiting = await approvals.WaitingSinceAsync(VendorAccountService.DebitNoteKind, nameof(VendorDebitNote), result.Items.Select(n => n.Id).ToList(), ct);
        var names = await NamesAsync(db, result.Items.Select(n => n.VendorId), result.Items.Select(n => n.StoreId), ct);
        return TypedResults.Ok(result.Map(n => ToDto(n, names, waiting)));
    }

    private static async Task<IResult> GetNote(Guid id, HospitalDbContext db, IApprovals approvals, CancellationToken ct)
    {
        var note = await db.Set<VendorDebitNote>().AsNoTracking().Include(n => n.Lines).FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null)
        {
            return Error.NotFound(nameof(VendorDebitNote), id).ToProblem();
        }

        var waiting = await approvals.WaitingSinceAsync(VendorAccountService.DebitNoteKind, nameof(VendorDebitNote), [id], ct);
        return TypedResults.Ok(ToDto(note, await NamesAsync(db, [note.VendorId], [note.StoreId], ct), waiting));
    }

    private static async Task<IResult> ListAdjustments(
        [AsParameters] PageQuery query, VendorDocumentStatus? status, Guid? vendorId, HospitalDbContext db, IApprovals approvals, CancellationToken ct)
    {
        var page = query.ToRequest();
        var adjustments = db.Set<VendorPayableAdjustment>().AsNoTracking();
        if (status is not null)
        {
            adjustments = adjustments.Where(a => a.Status == status);
        }

        if (vendorId is not null)
        {
            adjustments = adjustments.Where(a => a.VendorId == vendorId);
        }

        var result = await adjustments.OrderByDescending(a => a.RaisedAt).ToPagedResultAsync(page, ct);
        var waiting = await approvals.WaitingSinceAsync(
            VendorAccountService.AdjustmentKind, nameof(VendorPayableAdjustment), result.Items.Select(a => a.Id).ToList(), ct);
        var names = await NamesAsync(db, result.Items.Select(a => a.VendorId), [], ct);
        return TypedResults.Ok(result.Map(a => new PayableAdjustmentDto(
            a.Id, a.Number, a.VendorId, names.GetValueOrDefault(a.VendorId) ?? string.Empty, a.Direction, a.Amount, a.Reason, a.VendorReference, a.Status,
            a.RaisedAt, a.PostedAt, a.CancelledAt, a.CancelReason, waiting.TryGetValue(a.Id, out var since) ? since : null, a.Version)));
    }

    private static DebitNoteDto ToDto(VendorDebitNote n, IReadOnlyDictionary<Guid, string> names, IReadOnlyDictionary<Guid, DateTimeOffset> waiting) => new(
        n.Id, n.Number, n.VendorId, names.GetValueOrDefault(n.VendorId) ?? string.Empty, n.StoreId, names.GetValueOrDefault(n.StoreId) ?? string.Empty,
        n.GoodsReceiptId, n.VendorReference, n.Reason, n.Status, n.RaisedAt, n.PostedAt, n.CancelledAt, n.CancelReason, n.TaxableAmount,
        n.CgstAmount + n.SgstAmount + n.IgstAmount, n.TotalAmount, waiting.TryGetValue(n.Id, out var since) ? since : null,
        n.Lines.OrderBy(l => l.ItemName).Select(l => new DebitNoteLineDto(
            l.Id, l.StockBatchId, l.ItemId, l.ItemName, l.BatchNumber, l.ExpiryDate, l.Quantity, l.UnitCost, l.GstRate, l.TaxableAmount,
            l.CgstAmount + l.SgstAmount + l.IgstAmount)).ToList(),
        n.Version);

    private static async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        HospitalDbContext db, IEnumerable<Guid> vendorIds, IEnumerable<Guid> storeIds, CancellationToken ct)
    {
        var vendors = vendorIds.Distinct().ToList();
        var stores = storeIds.Distinct().ToList();
        var names = await db.Set<Vendor>().AsNoTracking().Where(v => vendors.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        foreach (var (id, name) in await db.Set<Store>().AsNoTracking().Where(s => stores.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct))
        {
            names[id] = name;
        }

        return names;
    }
}
