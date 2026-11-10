using System.Globalization;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>The goods receipt a second person is asked to post into stock.</summary>
public sealed record GoodsReceiptPostingPayload(Guid GoodsReceiptId);

/// <summary>
/// Goods receipt (INV-03): validates the vendor invoice (duplicate invoice, drug licence, expiry, rate ≤ MRP),
/// converts purchase units to base units and posts batches through <see cref="StockService"/>, updating the
/// purchase order's received quantities in the same transaction. A receipt goes into stock only when someone other than
/// whoever entered it, and other than whoever asked, approves it (maker-checker, <see cref="SupplyChainPermissions.GrnApprove"/>).
/// </summary>
public sealed class GoodsReceiptService(
    HospitalDbContext db, INumberSeries numbers, StockService stock, ICurrentUser currentUser, IApprovals approvals, TimeProvider time)
{
    public const string ReferenceModule = "supplychain.grn";

    /// <summary>The approval that posts a goods receipt into stock.</summary>
    public const string ApprovalKind = "purchase.grn";

    public async Task<Result<GoodsReceiptCreated>> CreateAsync(CreateGoodsReceiptRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var today = HospitalClock.Today(time);
        var receivedOn = request.ReceivedOn ?? today;
        if (receivedOn > today)
        {
            return Error.Field("receivedOn", "The receipt date cannot be in the future.");
        }

        var vendor = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == request.VendorId, ct);
        if (vendor is null || !vendor.IsActive)
        {
            return Error.Field("vendorId", "Choose an active vendor.");
        }

        if (!await db.Set<Store>().AnyAsync(s => s.Id == request.StoreId && s.IsActive, ct))
        {
            return Error.Field("storeId", "Choose an active store of this facility.");
        }

        var invoice = request.VendorInvoiceNumber.Trim().ToUpperInvariant();
        if (await db.Set<GoodsReceiptNote>().AnyAsync(g => g.VendorId == vendor.Id && g.VendorInvoiceNumber == invoice && g.Status != GoodsReceiptStatus.Cancelled, ct))
        {
            return Error.Conflict("grn.duplicate_invoice", $"Invoice {invoice} from {vendor.Name} has already been received.");
        }

        var itemIds = request.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Set<Item>().AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        if (itemIds.Any(id => !items.TryGetValue(id, out var item) || !item.IsActive))
        {
            return Error.Field("lines", "One of the items is unknown or inactive.");
        }

        // Drugs may only be bought from a supplier holding a valid wholesale drug licence (Form 20B/21B).
        if (items.Values.Any(i => i.Type == ItemType.Drug)
            && (string.IsNullOrWhiteSpace(vendor.DrugLicenseNumber) || vendor.DrugLicenseValidUntil < receivedOn))
        {
            return Error.Rule("grn.vendor_licence", $"{vendor.Name} has no valid drug licence on record; medicines cannot be received from this vendor.");
        }

        PurchaseOrder? order = null;
        if (request.PurchaseOrderId is { } orderId)
        {
            order = await db.Set<PurchaseOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is null)
            {
                return Error.NotFound(nameof(PurchaseOrder), orderId);
            }

            if (order.VendorId != vendor.Id || order.StoreId != request.StoreId)
            {
                return Error.Rule("grn.order_mismatch", $"Purchase order {order.Number} is for a different vendor or store.");
            }

            if (!order.IsReceivable)
            {
                return Error.Rule("po.not_receivable", $"Purchase order {order.Number} is {order.Status} and cannot receive goods.");
            }
        }

        var facilityGstin = await db.Set<Facility>().Where(f => f.Id == facilityId).Select(f => f.Gstin).FirstOrDefaultAsync(ct);
        var grn = new GoodsReceiptNote
        {
            FacilityId = facilityId,
            VendorId = vendor.Id,
            StoreId = request.StoreId,
            PurchaseOrderId = order?.Id,
            VendorInvoiceNumber = invoice,
            VendorInvoiceDate = request.VendorInvoiceDate,
            VendorInvoiceAmount = request.VendorInvoiceAmount,
            ReceivedOn = receivedOn,
            IsInterState = Vendor.IsInterState(vendor.Gstin, facilityGstin),
            Remarks = request.Remarks?.Trim(),
        };

        foreach (var input in request.Lines)
        {
            var item = items[input.ItemId];
            if (input.PurchaseOrderLineId is { } orderLineId && order?.Lines.FirstOrDefault(l => l.Id == orderLineId && l.ItemId == item.Id) is null)
            {
                return Error.Rule("grn.order_line_mismatch", $"{item.Name} does not match a line of the purchase order.");
            }

            grn.Lines.Add(new GoodsReceiptLine
            {
                GoodsReceiptNoteId = grn.Id,
                ItemId = item.Id,
                ItemName = item.Name,
                PurchaseOrderLineId = input.PurchaseOrderLineId,
                BatchNumber = StockService.NormalizeBatch(input.BatchNumber),
                ManufactureDate = input.ManufactureDate,
                ExpiryDate = input.ExpiryDate,
                Quantity = input.Quantity,
                FreeQuantity = input.FreeQuantity,
                Rate = input.Rate,
                Mrp = input.Mrp,
                DiscountPercent = input.DiscountPercent,
                GstRate = input.GstRate ?? item.GstRate,
                UnitsPerPurchaseUnit = item.PurchaseUnitFactor,
                RequiresBatch = item.TracksExpiry,
                // An implant's MRP is what its bill may charge at most (NPPA), so its batch must carry one, as a medicine's does.
                RequiresMrp = item.Type is ItemType.Drug or ItemType.Implant,
                AcceptShortExpiry = input.AcceptShortExpiry,
                CeilingPrice = PriceCeiling.AppliesTo(item.Type, item.CeilingPrice) ? item.CeilingPrice : null,
            });
        }

        grn.Recalculate();
        var valid = grn.Validate();
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        // The receipt, and the request for a second person to post it, are kept together or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        grn.Number = await numbers.NextAsync(SupplyChainNumberSeries.GoodsReceipt, facilityId, ct);
        db.Add(grn);
        await db.SaveChangesAsync(ct);
        Guid? approvalId = null;
        if (request.SendForApproval)
        {
            var asked = await AskCoreAsync(grn, ct);
            if (asked.IsFailure)
            {
                return asked.Error!;
            }

            approvalId = asked.Value.Id;
        }

        await transaction.CommitAsync(ct);
        return new GoodsReceiptCreated(grn.Id, grn.Number, grn.Status, grn.NetAmount, approvalId);
    }

    /// <summary>Asks for someone other than whoever entered the draft to check it against the supplier's invoice and post it.</summary>
    public async Task<Result<ApprovalRequestDto>> AskApprovalAsync(Guid id, CancellationToken ct)
    {
        var grn = await db.Set<GoodsReceiptNote>().AsNoTracking().Include(g => g.Lines).FirstOrDefaultAsync(g => g.Id == id, ct);
        if (grn is null)
        {
            return Error.NotFound(nameof(GoodsReceiptNote), id);
        }

        if (grn.Status != GoodsReceiptStatus.Draft)
        {
            return Error.Rule("grn.not_draft", $"GRN {grn.Number} is already {grn.Status}.");
        }

        var valid = grn.Validate();
        return valid.IsFailure ? valid.Error! : await AskCoreAsync(grn, ct);
    }

    /// <summary>When a request to post each receipt into stock has been waiting since, for those with one waiting.</summary>
    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> ApprovalWaitingSinceAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        approvals.WaitingSinceAsync(ApprovalKind, nameof(GoodsReceiptNote), ids, ct);

    /// <summary>
    /// Posts an approved receipt into stock, in the approval's transaction (the approval saves): the receipt as it was when
    /// it was sent for approval, or not at all. The checker is the one who posts, and may not be whoever entered it.
    /// </summary>
    internal async Task<Result<string>> PostApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<GoodsReceiptPostingPayload>(approval.Payload);
        var grn = await db.Set<GoodsReceiptNote>().Include(g => g.Lines).FirstOrDefaultAsync(g => g.Id == asked.GoodsReceiptId, ct);
        if (grn is null)
        {
            return Error.NotFound(nameof(GoodsReceiptNote), asked.GoodsReceiptId);
        }

        if (grn.Version != approval.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"GRN {grn.Number} changed after it was sent for approval (it is {grn.Status} now), so nothing went into stock.");
        }

        PurchaseOrder? order = null;
        if (grn.PurchaseOrderId is { } orderId)
        {
            order = await db.Set<PurchaseOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        }

        var posted = await PostCoreAsync(grn, order, ct);
        return posted.IsFailure ? posted.Error! : $"GRN {grn.Number} posted into stock";
    }

    public async Task<Result> CancelAsync(Guid id, string reason, CancellationToken ct)
    {
        var grn = await db.Set<GoodsReceiptNote>().FirstOrDefaultAsync(g => g.Id == id, ct);
        if (grn is null)
        {
            return Error.NotFound(nameof(GoodsReceiptNote), id);
        }

        var result = grn.Cancel(reason.Trim(), time.GetUtcNow());
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// Asks a second person to post the receipt as saved, with what they check it against: the supplier's invoice as
    /// printed against the total worked out from the lines, the order it was received against, and short-dated goods accepted.
    /// </summary>
    private async Task<Result<ApprovalRequestDto>> AskCoreAsync(GoodsReceiptNote grn, CancellationToken ct)
    {
        var vendor = await db.Set<Vendor>().AsNoTracking().Where(v => v.Id == grn.VendorId).Select(v => v.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var store = await db.Set<Store>().AsNoTracking().Where(s => s.Id == grn.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var order = grn.PurchaseOrderId is { } orderId
            ? await db.Set<PurchaseOrder>().AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Number).FirstOrDefaultAsync(ct)
            : null;
        var shortDated = grn.Lines.Where(l => l.AcceptShortExpiry).Select(l => l.ItemName).ToList();
        List<ApprovalDetail> details =
        [
            new("GRN", grn.Number),
            new("Supplier", vendor),
            new("Supplier's invoice", grn.VendorInvoiceDate is { } invoiceDate
                ? $"{grn.VendorInvoiceNumber} of {DocumentFormat.Date(invoiceDate)}"
                : grn.VendorInvoiceNumber),
            new("Received", $"{DocumentFormat.Date(grn.ReceivedOn)}, into {store}"),
            new("Against purchase order", order ?? "None"),
            new("Lines", grn.Lines.Count.ToString(CultureInfo.InvariantCulture)),
            new("Total worked out from the lines", DocumentFormat.Money(grn.NetAmount)),
            new("Invoice total as printed", grn.VendorInvoiceAmount is { } printed ? DocumentFormat.Money(printed) : "Not entered"),
        ];
        if (grn.VendorInvoiceAmount is { } invoiceTotal)
        {
            details.Add(new("Difference", invoiceTotal == grn.NetAmount ? "None" : DocumentFormat.Money(grn.NetAmount - invoiceTotal)));
        }

        if (shortDated.Count > 0)
        {
            details.Add(new("Accepted though expiring within 90 days", string.Join(", ", shortDated)));
        }

        return await approvals.RequestAsync(
            ApprovalKind,
            new ApprovalSubject(nameof(GoodsReceiptNote), grn.Id, grn.Version, grn.FacilityId, null),
            new GoodsReceiptPostingPayload(grn.Id),
            Fit($"GRN {grn.Number}: {vendor}, invoice {grn.VendorInvoiceNumber}, {DocumentFormat.Money(grn.NetAmount)}", 300),
            details,
            "Goods go into stock only when someone other than whoever entered them has checked them against the supplier's invoice.",
            ct);
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private async Task<Result> PostCoreAsync(GoodsReceiptNote grn, PurchaseOrder? order, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var posted = grn.MarkPosted(currentUser.UserId, now);
        if (posted.IsFailure)
        {
            return posted;
        }

        // The ceiling as it is now: one lowered since the receipt was entered refuses a pack priced above it.
        var lineItems = grn.Lines.Select(l => l.ItemId).Distinct().ToList();
        var ceilings = await db.Set<Item>().AsNoTracking().Where(i => lineItems.Contains(i.Id))
            .Select(i => new { i.Id, i.Type, i.CeilingPrice })
            .ToDictionaryAsync(i => i.Id, i => PriceCeiling.AppliesTo(i.Type, i.CeilingPrice) ? i.CeilingPrice : null, ct);
        foreach (var line in grn.Lines)
        {
            var priced = line.CheckCeiling(ceilings.GetValueOrDefault(line.ItemId));
            if (priced.IsFailure)
            {
                return priced;
            }
        }

        var reference = new StockReference(ReferenceModule, grn.Id, grn.Number, $"Invoice {grn.VendorInvoiceNumber}");
        foreach (var line in grn.Lines)
        {
            if (order is not null && line.PurchaseOrderLineId is { } orderLineId)
            {
                var received = order.RecordReceipt(orderLineId, line.Quantity, line.FreeQuantity, now);
                if (received.IsFailure)
                {
                    return received;
                }
            }

            var movement = await stock.ReceiveAsync(
                new StockReceipt(grn.StoreId, line.ItemId, line.BatchNumber, line.ExpiryDate, line.ManufactureDate, line.BaseQuantity, line.UnitMrp,
                    line.UnitCost, line.GstRate, grn.VendorId, grn.Id, grn.ReceivedOn),
                StockMovementType.Grn, reference, null, ct);
            if (movement.IsFailure)
            {
                return movement.Error!;
            }

            line.StockBatchId = movement.Value.BatchId;
        }

        return Result.Success();
    }
}

/// <summary>A goods receipt posted into stock: the store enters it, someone else checks it against the invoice and posts it.</summary>
internal sealed class GoodsReceiptApproval(GoodsReceiptService receipts) : IApprovalAction
{
    public string Kind => GoodsReceiptService.ApprovalKind;

    public string Title => "Goods receipt into stock";

    public string ApprovePermission => SupplyChainPermissions.GrnApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => receipts.PostApprovedAsync(request, ct);
}
