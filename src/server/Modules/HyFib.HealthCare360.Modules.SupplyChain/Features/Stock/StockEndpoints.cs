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
            .RequireAnyPermission(SupplyChainPermissions.ItemsManage, SupplyChainPermissions.AdjustmentsApprove);
    }

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

        var positions = db.Set<StockBatch>().AsNoTracking()
            .Where(b => b.StoreId == storeId && b.QuantityOnHand > 0)
            .GroupBy(b => b.ItemId)
            .Select(g => new
            {
                ItemId = g.Key,
                OnHand = g.Sum(b => b.QuantityOnHand),
                Reserved = g.Sum(b => b.QuantityReserved),
                Expired = g.Sum(b => b.ExpiryDate < today ? b.QuantityOnHand : 0m),
                Value = g.Sum(b => b.QuantityOnHand * b.PurchaseRate),
                NearestExpiry = g.Min(b => b.ExpiryDate),
            });

        var rows =
            from p in positions
            join i in items on p.ItemId equals i.Id
            select new { i.Id, i.Code, i.Name, i.Type, i.BaseUnit, i.ReorderLevel, p.OnHand, p.Reserved, p.Expired, p.Value, p.NearestExpiry };
        if (belowReorder == true)
        {
            rows = rows.Where(r => r.ReorderLevel > 0 && r.OnHand - r.Reserved - r.Expired <= r.ReorderLevel);
        }

        var result = await rows.OrderBy(r => r.Name).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(r =>
        {
            var available = r.OnHand - r.Reserved - r.Expired;
            return new StockSummaryRow(r.Id, r.Code, r.Name, r.Type, r.BaseUnit, r.OnHand, r.Reserved, Math.Max(available, 0), r.Expired,
                r.NearestExpiry, Money.Round(r.Value), r.ReorderLevel, r.ReorderLevel > 0 && available <= r.ReorderLevel);
        }));
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

        var result = batch.Block(request.Reason);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "stock_batch.blocked", $"Batch {batch.BatchNumber} blocked: {batch.BlockReason}")
        {
            EntityType = nameof(StockBatch),
            EntityId = batch.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Unblock(Guid id, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var batch = await db.Set<StockBatch>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (batch is null)
        {
            return Error.NotFound(nameof(StockBatch), id).ToProblem();
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
