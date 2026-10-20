using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Purchase requisitions and purchase orders (INV-03). Numbers are allocated inside the insert transaction so
/// they stay gap-free; GST on orders is split CGST/SGST or IGST from the vendor's and facility's GSTINs.
/// </summary>
public sealed class PurchaseService(HospitalDbContext db, INumberSeries numbers, ICurrentUser currentUser, TimeProvider time)
{
    // ------------------------------------------------------------------ requisitions

    public async Task<Result<DocumentCreated>> CreateRequisitionAsync(CreateRequisitionRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<Store>().AnyAsync(s => s.Id == request.StoreId && s.IsActive, ct))
        {
            return Error.Field("storeId", "Choose an active store of this facility.");
        }

        var items = await ActiveItemsAsync(request.Lines.Select(l => l.ItemId), ct);
        if (items.IsFailure)
        {
            return items.Error!;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var requisition = new PurchaseRequisition
        {
            FacilityId = facilityId,
            Number = await numbers.NextAsync(SupplyChainNumberSeries.PurchaseRequisition, facilityId, ct),
            StoreId = request.StoreId,
            RequiredBy = request.RequiredBy,
            Remarks = request.Remarks?.Trim(),
            RequestedByUserId = currentUser.UserId,
            Lines = request.Lines.Select(l => new PurchaseRequisitionLine
            {
                ItemId = l.ItemId,
                ItemName = items.Value[l.ItemId].Name,
                Quantity = l.Quantity,
                Remarks = l.Remarks?.Trim(),
            }).ToList(),
        };
        db.Add(requisition);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DocumentCreated(requisition.Id, requisition.Number);
    }

    /// <summary>One click from the reorder report: a requisition for every item at or below its reorder level.</summary>
    public async Task<Result<DocumentCreated>> CreateRequisitionFromReorderAsync(Guid storeId, InventoryReportService reports, CancellationToken ct)
    {
        var rows = await reports.ReorderAsync(storeId, ct);
        if (rows.Count == 0)
        {
            return Error.Rule("requisition.nothing_to_order", "No item in this store is at or below its reorder level.");
        }

        var lines = rows.Take(ReorderCalculator.MaxRequisitionLines)
            .Select(r => new RequisitionLineInput(r.ItemId, ReorderCalculator.RequisitionQuantity(r.SuggestedPurchaseUnits), "Reorder level reached"))
            .ToList();
        return await CreateRequisitionAsync(new CreateRequisitionRequest(storeId, null, "Generated from the reorder report", lines), ct);
    }

    public Task<Result> ApproveRequisitionAsync(Guid id, CancellationToken ct) =>
        ChangeRequisitionAsync(id, r => r.Approve(currentUser.UserId, time.GetUtcNow()), ct);

    public Task<Result> RejectRequisitionAsync(Guid id, string reason, CancellationToken ct) =>
        ChangeRequisitionAsync(id, r => r.Reject(currentUser.UserId, reason.Trim(), time.GetUtcNow()), ct);

    public Task<Result> CancelRequisitionAsync(Guid id, string reason, CancellationToken ct) =>
        ChangeRequisitionAsync(id, r => r.Cancel(reason.Trim(), time.GetUtcNow()), ct);

    // ------------------------------------------------------------------ purchase orders

    public async Task<Result<DocumentCreated>> CreateOrderAsync(SavePurchaseOrderRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var context = await LoadOrderContextAsync(facilityId, request, ct);
        if (context.IsFailure)
        {
            return context.Error!;
        }

        PurchaseRequisition? requisition = null;
        if (request.RequisitionId is { } requisitionId)
        {
            requisition = await db.Set<PurchaseRequisition>().FirstOrDefaultAsync(r => r.Id == requisitionId, ct);
            if (requisition is null)
            {
                return Error.NotFound(nameof(PurchaseRequisition), requisitionId);
            }

            if (requisition.StoreId != request.StoreId)
            {
                return Error.Rule("po.requisition_store", "The order must deliver to the store that raised the requisition.");
            }
        }

        var (vendor, items, interState) = context.Value;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var order = new PurchaseOrder
        {
            FacilityId = facilityId,
            Number = await numbers.NextAsync(SupplyChainNumberSeries.PurchaseOrder, facilityId, ct),
            VendorId = vendor.Id,
            StoreId = request.StoreId,
            RequisitionId = request.RequisitionId,
            OrderDate = HospitalClock.Today(time),
        };
        Apply(order, request, vendor, items, interState);
        db.Add(order);

        if (requisition is not null)
        {
            var marked = requisition.MarkOrdered(order.Id);
            if (marked.IsFailure)
            {
                return marked.Error!;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DocumentCreated(order.Id, order.Number);
    }

    /// <summary>Replaces a draft order's header and lines (optimistic concurrency on the version the user edited).</summary>
    public async Task<Result> UpdateOrderAsync(Guid id, SavePurchaseOrderRequest request, CancellationToken ct)
    {
        var order = await db.Set<PurchaseOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null)
        {
            return Error.NotFound(nameof(PurchaseOrder), id);
        }

        if (order.Status != PurchaseOrderStatus.Draft)
        {
            return Error.Rule("po.not_draft", "Only draft purchase orders can be edited.");
        }

        var context = await LoadOrderContextAsync(order.FacilityId, request, ct);
        if (context.IsFailure)
        {
            return context.Error!;
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(order, version);
        }

        var (vendor, items, interState) = context.Value;
        order.VendorId = vendor.Id;
        order.StoreId = request.StoreId;
        order.Lines.Clear();
        Apply(order, request, vendor, items, interState);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public Task<Result> SubmitOrderAsync(Guid id, CancellationToken ct) => ChangeOrderAsync(id, o => o.Submit(time.GetUtcNow()), ct);

    public Task<Result> ApproveOrderAsync(Guid id, CancellationToken ct) => ChangeOrderAsync(id, o => o.Approve(currentUser.UserId, time.GetUtcNow()), ct);

    public Task<Result> ReturnOrderAsync(Guid id, string note, CancellationToken ct) => ChangeOrderAsync(id, o => o.ReturnToDraft(note.Trim()), ct);

    public Task<Result> SendOrderAsync(Guid id, CancellationToken ct) => ChangeOrderAsync(id, o => o.MarkSent(time.GetUtcNow()), ct);

    public Task<Result> CloseOrderAsync(Guid id, string reason, CancellationToken ct) => ChangeOrderAsync(id, o => o.Close(reason.Trim(), time.GetUtcNow()), ct);

    public Task<Result> CancelOrderAsync(Guid id, string reason, CancellationToken ct) => ChangeOrderAsync(id, o => o.Cancel(reason.Trim(), time.GetUtcNow()), ct);

    // ------------------------------------------------------------------ helpers

    private async Task<Result<(Vendor Vendor, Dictionary<Guid, Item> Items, bool InterState)>> LoadOrderContextAsync(
        Guid facilityId, SavePurchaseOrderRequest request, CancellationToken ct)
    {
        var vendor = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == request.VendorId, ct);
        if (vendor is null || !vendor.IsActive)
        {
            return Error.Field("vendorId", "Choose an active vendor.");
        }

        if (!await db.Set<Store>().AnyAsync(s => s.Id == request.StoreId && s.IsActive, ct))
        {
            return Error.Field("storeId", "Choose an active store of this facility.");
        }

        var items = await ActiveItemsAsync(request.Lines.Select(l => l.ItemId), ct);
        if (items.IsFailure)
        {
            return items.Error!;
        }

        var facilityGstin = await db.Set<Facility>().Where(f => f.Id == facilityId).Select(f => f.Gstin).FirstOrDefaultAsync(ct);
        return (vendor, items.Value, Vendor.IsInterState(vendor.Gstin, facilityGstin));
    }

    private static void Apply(PurchaseOrder order, SavePurchaseOrderRequest request, Vendor vendor, Dictionary<Guid, Item> items, bool interState)
    {
        order.ExpectedDeliveryDate = request.ExpectedDeliveryDate;
        order.PaymentTermsDays = request.PaymentTermsDays ?? vendor.PaymentTermsDays;
        order.Remarks = request.Remarks?.Trim();
        order.IsInterState = interState;
        foreach (var line in request.Lines)
        {
            var item = items[line.ItemId];
            order.Lines.Add(new PurchaseOrderLine
            {
                PurchaseOrderId = order.Id,
                ItemId = item.Id,
                ItemName = item.Name,
                PurchaseUnit = item.PurchaseUnit,
                Quantity = line.Quantity,
                FreeQuantity = line.FreeQuantity,
                Rate = line.Rate,
                DiscountPercent = line.DiscountPercent,
                GstRate = line.GstRate ?? item.GstRate,
            });
        }

        order.Recalculate();
    }

    private async Task<Result<Dictionary<Guid, Item>>> ActiveItemsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var idList = ids.Distinct().ToList();
        var items = await db.Set<Item>().AsNoTracking().Where(i => idList.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var missing = idList.FirstOrDefault(id => !items.TryGetValue(id, out var item) || !item.IsActive);
        return missing == Guid.Empty ? items : Error.Field("lines", "One of the items is unknown or inactive.");
    }

    private async Task<Result> ChangeRequisitionAsync(Guid id, Func<PurchaseRequisition, Result> change, CancellationToken ct)
    {
        var requisition = await db.Set<PurchaseRequisition>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (requisition is null)
        {
            return Error.NotFound(nameof(PurchaseRequisition), id);
        }

        var result = change(requisition);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    private async Task<Result> ChangeOrderAsync(Guid id, Func<PurchaseOrder, Result> change, CancellationToken ct)
    {
        var order = await db.Set<PurchaseOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null)
        {
            return Error.NotFound(nameof(PurchaseOrder), id);
        }

        var result = change(order);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }
}
