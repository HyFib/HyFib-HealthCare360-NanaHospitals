using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Reports;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Inventory reports (INV-08, PH-04): near-expiry and expired stock, reorder suggestions, valuation, a store
/// dashboard and ABC/VED/FSN analysis. Aggregation runs in PostgreSQL; classification uses the pure calculators
/// in <see cref="AbcAnalysis"/>, <see cref="FsnAnalysis"/> and <see cref="VedAnalysis"/>.
/// </summary>
public sealed class InventoryReportService(HospitalDbContext db, TimeProvider time)
{
    public const int DefaultNearExpiryDays = 90;

    public async Task<ExpiryReport> NearExpiryAsync(Guid? storeId, int withinDays, CancellationToken ct)
    {
        var today = HospitalClock.Today(time);
        var batches = await InventoryQueries.ExpiringAsync(db, StoreFilter(storeId), today, withinDays, includeExpired: false, ct);
        return new ExpiryReport(today, withinDays, batches.Count, batches.Sum(b => b.ValueAtCost), batches.Sum(b => b.ValueAtMrp), batches);
    }

    /// <summary>Batches whose expiry day has passed but still hold stock (candidates for an expiry write-off).</summary>
    public async Task<ExpiryReport> ExpiredAsync(Guid? storeId, CancellationToken ct)
    {
        var today = HospitalClock.Today(time);
        var batches = await InventoryQueries.ExpiringAsync(db, StoreFilter(storeId), today, -1, includeExpired: true, ct);
        return new ExpiryReport(today, 0, batches.Count, batches.Sum(b => b.ValueAtCost), batches.Sum(b => b.ValueAtMrp), batches);
    }

    /// <summary>
    /// Items whose usable stock plus what is already on order is at or below the reorder level, topped up to the
    /// maximum stock, vital items first (<see cref="ReorderCalculator"/>). Stock and orders are the store's, or every
    /// store's without one.
    /// </summary>
    public async Task<IReadOnlyList<ReorderRow>> ReorderAsync(Guid? storeId, CancellationToken ct)
    {
        var today = HospitalClock.Today(time);
        var items = await db.Set<Item>().AsNoTracking().Where(i => i.IsActive && i.ReorderLevel > 0).ToListAsync(ct);

        var usable = db.Set<StockBatch>().AsNoTracking()
            .Where(b => b.QuantityOnHand > b.QuantityReserved && !b.IsBlocked && (b.ExpiryDate == null || b.ExpiryDate >= today));
        if (storeId is not null)
        {
            usable = usable.Where(b => b.StoreId == storeId);
        }

        var available = await usable.GroupBy(b => b.ItemId)
            .Select(g => new { ItemId = g.Key, Quantity = g.Sum(b => b.QuantityOnHand - b.QuantityReserved) })
            .ToDictionaryAsync(x => x.ItemId, x => x.Quantity, ct);

        var onOrder = await (
                from line in db.Set<PurchaseOrderLine>()
                join order in db.Set<PurchaseOrder>() on line.PurchaseOrderId equals order.Id
                where (order.Status == PurchaseOrderStatus.Approved || order.Status == PurchaseOrderStatus.Sent || order.Status == PurchaseOrderStatus.PartiallyReceived)
                      && (storeId == null || order.StoreId == storeId)
                group line by line.ItemId into g
                select new { ItemId = g.Key, Pending = g.Sum(l => l.Quantity - l.ReceivedQuantity) })
            .ToDictionaryAsync(x => x.ItemId, x => x.Pending, ct);

        var suggestions = ReorderCalculator.Rank(items
            .Select(i => ReorderCalculator.Suggest(i, available.GetValueOrDefault(i.Id), onOrder.GetValueOrDefault(i.Id)))
            .OfType<ReorderSuggestion>());

        var candidateIds = suggestions.Select(s => s.Item.Id).ToList();
        var costs = await db.Set<StockBatch>().AsNoTracking()
            .Where(b => candidateIds.Contains(b.ItemId) && (storeId == null || b.StoreId == storeId))
            .Select(b => new { b.ItemId, b.ReceivedOn, b.PurchaseRate })
            .ToListAsync(ct);
        var lastCost = costs.GroupBy(c => c.ItemId).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.ReceivedOn).First().PurchaseRate);

        return suggestions
            .Select(s => new ReorderRow(s.Item.Id, s.Item.Code, s.Item.Name, s.Item.Type, s.Item.VedClass, s.Item.BaseUnit, s.Item.PurchaseUnit,
                s.Item.PurchaseUnitFactor, s.Available, s.OnOrder, s.Item.ReorderLevel, s.Item.ReorderQuantity, s.Item.MaximumStock, s.Quantity,
                s.PurchaseUnits, lastCost.TryGetValue(s.Item.Id, out var cost) ? cost : null))
            .ToList();
    }

    public async Task<ValuationReport> ValuationAsync(Guid? storeId, CancellationToken ct)
    {
        var batches = db.Set<StockBatch>().AsNoTracking().Where(b => b.QuantityOnHand > 0);
        if (storeId is not null)
        {
            batches = batches.Where(b => b.StoreId == storeId);
        }

        var grouped = await batches.GroupBy(b => new { b.StoreId, b.ItemId })
            .Select(g => new
            {
                g.Key.StoreId,
                g.Key.ItemId,
                Quantity = g.Sum(b => b.QuantityOnHand),
                Cost = g.Sum(b => b.QuantityOnHand * b.PurchaseRate),
                Mrp = g.Sum(b => b.QuantityOnHand * b.Mrp),
            })
            .ToListAsync(ct);

        var storeIds = grouped.Select(g => g.StoreId).Distinct().ToList();
        var itemIds = grouped.Select(g => g.ItemId).Distinct().ToList();
        var stores = await db.Set<Store>().AsNoTracking().Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var items = await db.Set<Item>().AsNoTracking().Where(i => itemIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => new { i.Code, i.Name, i.Type }, ct);

        var rows = grouped
            .Where(g => items.ContainsKey(g.ItemId))
            .Select(g => new ValuationRow(g.StoreId, stores.GetValueOrDefault(g.StoreId) ?? string.Empty, g.ItemId, items[g.ItemId].Code, items[g.ItemId].Name,
                items[g.ItemId].Type, g.Quantity, Money.Round(g.Cost), Money.Round(g.Mrp)))
            .OrderBy(r => r.StoreName)
            .ThenByDescending(r => r.ValueAtCost)
            .ToList();
        var byType = rows.GroupBy(r => r.Type)
            .Select(g => new ValuationByType(g.Key, g.Sum(r => r.ValueAtCost), g.Sum(r => r.ValueAtMrp)))
            .OrderByDescending(t => t.ValueAtCost)
            .ToList();
        return new ValuationReport(HospitalClock.Today(time), rows.Sum(r => r.ValueAtCost), rows.Sum(r => r.ValueAtMrp), byType, rows);
    }

    /// <summary>
    /// ABC (consumption value), FSN (issue frequency) and VED (clinical criticality) over a period. The item universe
    /// is everything consumed in the period plus everything still in stock, so stock that never moved shows as
    /// Non-moving. Consumption = issues, sales, dispenses and consumption net of returns; transfers are excluded.
    /// </summary>
    public async Task<InventoryAnalysis> AnalyseAsync(DateOnly from, DateOnly to, Guid? storeId, CancellationToken ct)
    {
        var (start, end) = HospitalClock.Range(from, to);
        var ledger = db.Set<StockLedgerEntry>().AsNoTracking()
            .Where(e => e.OccurredAt >= start && e.OccurredAt < end
                        && (e.MovementType == StockMovementType.Issue || e.MovementType == StockMovementType.Sale || e.MovementType == StockMovementType.Dispense
                            || e.MovementType == StockMovementType.Consumption || e.MovementType == StockMovementType.Return));
        var holding = db.Set<StockBatch>().AsNoTracking().Where(b => b.QuantityOnHand > 0);
        if (storeId is not null)
        {
            ledger = ledger.Where(e => e.StoreId == storeId);
            holding = holding.Where(b => b.StoreId == storeId);
        }

        var consumption = await ledger.GroupBy(e => e.ItemId)
            .Select(g => new
            {
                ItemId = g.Key,
                Quantity = -g.Sum(e => e.Quantity),
                Value = -g.Sum(e => e.Quantity * e.UnitCost),
                Issues = g.Count(e => e.Quantity < 0),
            })
            .ToDictionaryAsync(x => x.ItemId, ct);
        var held = await holding.Select(b => b.ItemId).Distinct().ToListAsync(ct);

        var universe = consumption.Keys.Union(held).ToList();
        var items = await db.Set<Item>().AsNoTracking().Where(i => universe.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        universe = universe.Where(items.ContainsKey).ToList();

        var abc = AbcAnalysis.Classify(universe.Select(id => (id, consumption.TryGetValue(id, out var c) ? Math.Max(c.Value, 0) : 0m)))
            .ToDictionary(r => r.ItemId);
        var fsn = FsnAnalysis.Classify(universe.Select(id => (id, consumption.TryGetValue(id, out var c) ? c.Issues : 0)))
            .ToDictionary(r => r.ItemId);

        var rows = universe
            .Select(id =>
            {
                var item = items[id];
                var used = consumption.GetValueOrDefault(id);
                var a = abc[id];
                return new AnalysisRow(id, item.Code, item.Name, item.Type, Math.Max(used?.Quantity ?? 0, 0), Money.Round(Math.Max(used?.Value ?? 0, 0)),
                    used?.Issues ?? 0, a.SharePercent, a.CumulativePercent, a.Class, item.VedClass, fsn[id].Class, VedAnalysis.Matrix(a.Class, item.VedClass));
            })
            .OrderByDescending(r => r.ConsumedValue)
            .ThenBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = rows.Sum(r => r.ConsumedValue);
        return new InventoryAnalysis(
            from, to, storeId, total,
            Summarise(rows, r => r.Abc.ToString(), total),
            Summarise(rows, r => r.Fsn.ToString(), total),
            Summarise(rows, r => r.AbcVedCategory.ToString(), total),
            rows);
    }

    public async Task<InventorySummary> SummaryAsync(Guid? storeId, CancellationToken ct)
    {
        var batches = db.Set<StockBatch>().AsNoTracking().Where(b => b.QuantityOnHand > 0);
        if (storeId is not null)
        {
            batches = batches.Where(b => b.StoreId == storeId);
        }

        var atCost = await batches.SumAsync(b => b.QuantityOnHand * b.PurchaseRate, ct);
        var atMrp = await batches.SumAsync(b => b.QuantityOnHand * b.Mrp, ct);
        var nearExpiry = await NearExpiryAsync(storeId, DefaultNearExpiryDays, ct);
        var expired = await ExpiredAsync(storeId, ct);
        var reorder = await ReorderAsync(storeId, ct);

        var pendingIndents = await db.Set<Indent>().CountAsync(
            i => (i.Status == IndentStatus.Pending || i.Status == IndentStatus.Approved || i.Status == IndentStatus.PartiallyIssued)
                 && (storeId == null || i.FromStoreId == storeId), ct);
        var pendingAdjustments = await db.Set<StockAdjustment>().CountAsync(
            a => a.Status == AdjustmentStatus.PendingApproval && (storeId == null || a.StoreId == storeId), ct);
        var openOrders = await db.Set<PurchaseOrder>().CountAsync(
            o => (o.Status == PurchaseOrderStatus.Submitted || o.Status == PurchaseOrderStatus.Approved || o.Status == PurchaseOrderStatus.Sent
                  || o.Status == PurchaseOrderStatus.PartiallyReceived) && (storeId == null || o.StoreId == storeId), ct);
        var inTransit = await db.Set<StockTransfer>().CountAsync(
            t => t.Status == TransferStatus.InTransit && (storeId == null || t.FromStoreId == storeId || t.ToStoreId == storeId), ct);

        return new InventorySummary(
            Money.Round(atCost), Money.Round(atMrp), nearExpiry.BatchCount, nearExpiry.ValueAtCost, expired.BatchCount, expired.ValueAtCost,
            reorder.Count, pendingIndents, pendingAdjustments, openOrders, inTransit);
    }

    private static IReadOnlyList<ClassSummary> Summarise(IReadOnlyList<AnalysisRow> rows, Func<AnalysisRow, string> classOf, decimal total) =>
        rows.GroupBy(classOf)
            .Select(g => new ClassSummary(g.Key, g.Count(), g.Sum(r => r.ConsumedValue), total == 0 ? 0 : Math.Round(g.Sum(r => r.ConsumedValue) * 100m / total, 2)))
            .OrderBy(s => s.Class, StringComparer.Ordinal)
            .ToList();

    private static Guid[]? StoreFilter(Guid? storeId) => storeId is { } id ? [id] : null;
}
