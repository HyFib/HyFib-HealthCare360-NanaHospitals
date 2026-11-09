using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Stock;

/// <summary>/api/v1/inventory/stock — stock position, batches, stock card (ledger), reservations and batch holds (INV-04).</summary>
internal static class StockEndpoints
{
    private static readonly string[] BatchReaders =
    [
        SupplyChainPermissions.StockRead, SupplyChainPermissions.IndentsIssue, SupplyChainPermissions.Transfers,
        SupplyChainPermissions.Counts, SupplyChainPermissions.GrnReceive, SupplyChainPermissions.AdjustmentsApprove,
    ];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inventory/stock").WithTags("Inventory");

        group.MapGet("/", Summary).RequirePermission(SupplyChainPermissions.StockRead);
        group.MapGet("/batches", Batches).RequireAnyPermission(BatchReaders);
        group.MapGet("/ledger", Ledger).RequirePermission(SupplyChainPermissions.StockRead);
        group.MapGet("/reservations", Reservations).RequireAnyPermission(BatchReaders);
        group.MapPost("/batches/{id:guid}/block", Block)
            .RequireAnyPermission(SupplyChainPermissions.ItemsManage, SupplyChainPermissions.AdjustmentsApprove)
            .WithValidation<BlockBatchRequest>();
        group.MapPost("/batches/{id:guid}/unblock", Unblock)
            .RequireAnyPermission(SupplyChainPermissions.ItemsManage, SupplyChainPermissions.AdjustmentsApprove)
            .WithValidation<UnblockBatchRequest>();
    }

    /// <summary>
    /// The store's stock, item by item. Available is usable stock: not reserved, not expired and not in a blocked batch (a
    /// recall, a quality hold). An item is below its reorder level by the reorder report's rule (<see cref="ReorderCalculator"/>):
    /// active, with a level, and its usable stock plus what is still due on the store's open purchase orders at or under it.
    /// Filtered to items below reorder, an item with no stock left at all is listed too, as the reorder report lists it.
    /// </summary>
    private static async Task<IResult> Summary(
        Guid storeId, [AsParameters] PageQuery query, ItemType? type, bool? belowReorder, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = HospitalClock.Today(time);
        var items = db.Set<Item>().AsNoTracking().Search(page.SearchTerm);
        if (type is not null)
        {
            items = items.Where(i => i.Type == type);
        }

        // The store's batches with stock, and what is still due on its approved, sent or part-received orders (in purchase
        // units), each summed per item as a correlated sub-query EF can translate.
        var batches = db.Set<StockBatch>().AsNoTracking().Where(b => b.StoreId == storeId && b.QuantityOnHand > 0);
        var pending =
            from line in db.Set<PurchaseOrderLine>()
            join order in db.Set<PurchaseOrder>() on line.PurchaseOrderId equals order.Id
            where order.StoreId == storeId
                  && (order.Status == PurchaseOrderStatus.Approved || order.Status == PurchaseOrderStatus.Sent || order.Status == PurchaseOrderStatus.PartiallyReceived)
                  && line.Quantity > line.ReceivedQuantity
            select line;

        // The store's own reorder level for an item overrides the item's (StoreItemLevel).
        var levels = db.Set<StoreItemLevel>().AsNoTracking().Where(l => l.StoreId == storeId);
        var listOutOfStock = belowReorder == true;
        var rows = items
            .Select(i => new
            {
                i.Id,
                i.Code,
                i.Name,
                i.Type,
                i.BaseUnit,
                ReorderLevel = levels.Where(l => l.ItemId == i.Id).Select(l => (decimal?)l.ReorderLevel).FirstOrDefault() ?? i.ReorderLevel,
                i.IsActive,
                HasStock = batches.Any(b => b.ItemId == i.Id),
                OnHand = batches.Where(b => b.ItemId == i.Id).Sum(b => (decimal?)b.QuantityOnHand) ?? 0m,
                Reserved = batches.Where(b => b.ItemId == i.Id).Sum(b => (decimal?)b.QuantityReserved) ?? 0m,
                Expired = batches.Where(b => b.ItemId == i.Id).Sum(b => (decimal?)(b.ExpiryDate < today ? b.QuantityOnHand : 0m)) ?? 0m,
                Blocked = batches.Where(b => b.ItemId == i.Id).Sum(b => (decimal?)(b.IsBlocked && !(b.ExpiryDate < today) ? b.QuantityOnHand : 0m)) ?? 0m,
                Usable = batches.Where(b => b.ItemId == i.Id).Sum(b => (decimal?)(!b.IsBlocked && (b.ExpiryDate == null || b.ExpiryDate >= today) && b.QuantityOnHand > b.QuantityReserved
                    ? b.QuantityOnHand - b.QuantityReserved
                    : 0m)) ?? 0m,
                Value = batches.Where(b => b.ItemId == i.Id).Sum(b => (decimal?)(b.QuantityOnHand * b.PurchaseRate)) ?? 0m,
                NearestExpiry = batches.Where(b => b.ItemId == i.Id).Min(b => b.ExpiryDate),
                OnOrder = (pending.Where(l => l.ItemId == i.Id).Sum(l => (decimal?)(l.Quantity - l.ReceivedQuantity)) ?? 0m) * i.PurchaseUnitFactor,
            });
        rows = rows.Where(r => r.HasStock || (listOutOfStock && r.IsActive && r.ReorderLevel > 0));
        if (belowReorder == true)
        {
            rows = rows.Where(r => r.IsActive && r.ReorderLevel > 0 && r.Usable + r.OnOrder <= r.ReorderLevel);
        }

        var result = await rows.OrderBy(r => r.Name).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(r => new StockSummaryRow(r.Id, r.Code, r.Name, r.Type, r.BaseUnit, r.OnHand, r.Reserved, r.Usable, r.Expired,
            r.NearestExpiry, Money.Round(r.Value), r.ReorderLevel, ReorderCalculator.IsDue(r.IsActive, r.ReorderLevel, r.Usable, r.OnOrder), r.Blocked, r.OnOrder)));
    }

    private static async Task<IResult> Batches(Guid storeId, Guid itemId, bool? includeEmpty, StockService stock, CancellationToken ct) =>
        TypedResults.Ok(await stock.GetBatchesAsync(storeId, itemId, includeEmpty == true, ct));

    /// <summary>Stock card: every movement with the running balance. Dates are local (IST) calendar dates.</summary>
    private static async Task<IResult> Ledger(
        [AsParameters] PageQuery query,
        Guid? storeId,
        Guid? itemId,
        DateOnly? from,
        DateOnly? to,
        StockMovementType? movement,
        HospitalDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = HospitalClock.Today(time);
        var (start, end) = HospitalClock.Range(from ?? today.AddDays(-30), to ?? today);
        var entries = db.Set<StockLedgerEntry>().AsNoTracking().Where(e => e.OccurredAt >= start && e.OccurredAt < end);
        if (storeId is not null)
        {
            entries = entries.Where(e => e.StoreId == storeId);
        }

        if (itemId is not null)
        {
            entries = entries.Where(e => e.ItemId == itemId);
        }

        if (movement is not null)
        {
            entries = entries.Where(e => e.MovementType == movement);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            entries = entries.Where(e => e.ReferenceNumber == upper || e.BatchNumber == upper);
        }

        var ordered = page.Desc ? entries.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id) : entries.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id);
        var result = await ordered
            .Select(e => new LedgerRow(
                e.Id, e.OccurredAt, e.MovementType, e.ItemId,
                db.Set<Item>().Where(i => i.Id == e.ItemId).Select(i => i.Name).FirstOrDefault() ?? string.Empty,
                e.BatchId, e.BatchNumber, e.ExpiryDate, e.Quantity > 0 ? e.Quantity : 0m, e.Quantity < 0 ? -e.Quantity : 0m, e.BalanceAfter,
                e.BatchBalanceAfter, e.UnitCost, e.ReferenceModule, e.ReferenceId, e.ReferenceNumber, e.Remarks,
                db.Set<User>().Where(u => u.Id == e.UserId).Select(u => u.DisplayName).FirstOrDefault(),
                e.CounterpartStoreId))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Reservations(Guid storeId, Guid? itemId, HospitalDbContext db, CancellationToken ct)
    {
        var reservations = db.Set<StockReservation>().AsNoTracking().Where(r => r.StoreId == storeId && r.Status == ReservationStatus.Active);
        if (itemId is not null)
        {
            reservations = reservations.Where(r => r.ItemId == itemId);
        }

        var rows = await (
                from r in reservations
                join i in db.Set<Item>() on r.ItemId equals i.Id
                join b in db.Set<StockBatch>() on r.BatchId equals b.Id
                orderby r.CreatedAt
                select new ReservationRow(r.Id, r.ItemId, i.Name, r.BatchId, b.BatchNumber, r.Quantity,
                    r.Quantity - r.ConsumedQuantity - r.ReleasedQuantity, r.ReferenceModule, r.ReferenceId, r.ReferenceNumber, r.CreatedAt))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    /// <summary>Quality hold / recall: the batch is excluded from every allocation until unblocked (explicitly audited).</summary>
    private static async Task<IResult> Block(Guid id, BlockBatchRequest request, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var batch = await db.Set<StockBatch>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
        {
            return Error.NotFound(nameof(StockBatch), id).ToProblem();
        }

        var result = batch.Block(request.Reason, request.Recall);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "stock_batch.blocked",
            $"Batch {batch.BatchNumber} {(batch.IsRecalled ? "recalled" : "blocked")}: {batch.BlockReason}")
        {
            EntityType = nameof(StockBatch),
            EntityId = batch.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { recall = batch.IsRecalled },
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// A quality hold is released at once. A recalled batch is released only when a second person agrees (202, MAKER-CHECKER
    /// slice 13): it goes back on the shelf to be sold, dispensed and issued to patients.
    /// </summary>
    private static async Task<IResult> Unblock(
        Guid id, UnblockBatchRequest request, HospitalDbContext db, IAuditLogger audit, RecallReleaseApprovals recalls, CancellationToken ct)
    {
        var batch = await db.Set<StockBatch>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
        {
            return Error.NotFound(nameof(StockBatch), id).ToProblem();
        }

        if (batch.IsRecalled)
        {
            var asked = await recalls.AskAsync(batch, request.Reason, ct);
            return asked.IsFailure ? asked.Error!.ToProblem() : TypedResults.Accepted($"/api/v1/approvals/{asked.Value.Id}", asked.Value);
        }

        batch.Unblock();
        audit.Add(new AuditEntry(AuditCategories.Data, "stock_batch.unblocked", $"Batch {batch.BatchNumber} released for use")
        {
            EntityType = nameof(StockBatch),
            EntityId = batch.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }
}
