using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Goods receipt (INV-03): validates the vendor invoice (duplicate invoice, drug licence, expiry, rate ≤ MRP),
/// converts purchase units to base units and posts batches through <see cref="StockService"/>, updating the
/// purchase order's received quantities in the same transaction.
/// </summary>
public sealed class GoodsReceiptService(HospitalDbContext db, INumberSeries numbers, StockService stock, ICurrentUser currentUser, TimeProvider time)
{
    public const string ReferenceModule = "supplychain.grn";

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
            });
        }

        grn.Recalculate();
        var valid = grn.Validate();
        if (valid.IsFailure)
        {
            return valid.Error!;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        grn.Number = await numbers.NextAsync(SupplyChainNumberSeries.GoodsReceipt, facilityId, ct);
        db.Add(grn);
        if (request.Post)
        {
            var posted = await PostCoreAsync(grn, order, ct);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return new GoodsReceiptCreated(grn.Id, grn.Number, grn.Status, grn.NetAmount);
    }

    /// <summary>Posts a verified draft GRN.</summary>
    public async Task<Result> PostAsync(Guid id, CancellationToken ct)
    {
        var grn = await db.Set<GoodsReceiptNote>().Include(g => g.Lines).FirstOrDefaultAsync(g => g.Id == id, ct);
        if (grn is null)
        {
            return Error.NotFound(nameof(GoodsReceiptNote), id);
        }

        PurchaseOrder? order = null;
        if (grn.PurchaseOrderId is { } orderId)
        {
            order = await db.Set<PurchaseOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var posted = await PostCoreAsync(grn, order, ct);
        if (posted.IsFailure)
        {
            return posted;
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved;
        }

        await transaction.CommitAsync(ct);
        return Result.Success();
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

    private async Task<Result> PostCoreAsync(GoodsReceiptNote grn, PurchaseOrder? order, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var posted = grn.MarkPosted(currentUser.UserId, now);
        if (posted.IsFailure)
        {
            return posted;
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
