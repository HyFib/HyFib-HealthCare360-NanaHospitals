using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>Compact item view shared with other modules (pharmacy counters, e-prescribing pickers, OT consumables).</summary>
public sealed record ItemSummary(
    Guid Id,
    string Code,
    string Name,
    string? GenericName,
    string? BrandName,
    string? Strength,
    string? DosageForm,
    ItemType Type,
    DrugSchedule Schedule,
    bool IsControlled,
    bool IsColdChain,
    string BaseUnit,
    string? HsnCode,
    decimal GstRate,
    bool IsActive,
    string? Manufacturer,
    string? Specification,
    decimal? CeilingPrice)
{
    public bool RequiresPrescription => Item.NeedsPrescription(Schedule, IsControlled);

    public bool RequiresRegister => Item.NeedsRegister(Schedule, IsControlled);
}

/// <summary>Stock position of one item in one store (what the counter can sell now and at which MRP).</summary>
public sealed record StockAvailability(Guid ItemId, decimal OnHand, decimal Available, DateOnly? NearestExpiry, decimal? CurrentMrp);

public sealed record ExpiringBatch(
    Guid BatchId,
    Guid StoreId,
    string StoreName,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    string BatchNumber,
    DateOnly ExpiryDate,
    int DaysToExpiry,
    decimal Quantity,
    decimal ValueAtCost,
    decimal ValueAtMrp);

/// <summary>Read-only inventory look-ups for this and higher-layer modules (no tracking, facility-scoped by the context).</summary>
public static class InventoryQueries
{
    /// <summary>
    /// Counter search: exact code or barcode, or name / generic / brand words (trigram-indexed). A scanned pharmacy tag
    /// (<see cref="BatchTag"/>) finds its item by the code it carries, or an item whose own barcode is the whole text
    /// (a barcode may hold a <c>#</c> too).
    /// </summary>
    public static IQueryable<Item> Search(this IQueryable<Item> items, string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return items;
        }

        term = term.Trim();
        if (BatchTag.TryParse(term, out var taggedCode, out _))
        {
            return items.Where(i => i.Code == taggedCode || i.Barcode == term);
        }

        var code = term.ToUpperInvariant();
        var like = term.ToLikePattern();
        return items.Where(i => i.Code == code || i.Barcode == term || EF.Functions.ILike(i.Name, like, QueryExtensions.LikeEscape)
                                || (i.GenericName != null && EF.Functions.ILike(i.GenericName, like, QueryExtensions.LikeEscape))
                                || (i.BrandName != null && EF.Functions.ILike(i.BrandName, like, QueryExtensions.LikeEscape)));
    }

    public static async Task<Dictionary<Guid, ItemSummary>> ItemSummariesAsync(HospitalDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var idList = ids.Distinct().ToList();
        return await db.Set<Item>().AsNoTracking()
            .Where(i => idList.Contains(i.Id))
            .Select(i => new ItemSummary(i.Id, i.Code, i.Name, i.GenericName, i.BrandName, i.Strength, i.DosageForm, i.Type, i.Schedule,
                i.IsControlled, i.IsColdChain, i.BaseUnit, i.HsnCode, i.GstRate, i.IsActive, i.Manufacturer, i.Specification, i.CeilingPrice))
            .ToDictionaryAsync(i => i.Id, ct);
    }

    /// <summary>On-hand, sellable quantity, nearest usable expiry and the MRP of the batch FEFO would pick first.</summary>
    public static async Task<Dictionary<Guid, StockAvailability>> AvailabilityAsync(
        HospitalDbContext db, Guid storeId, IReadOnlyCollection<Guid> itemIds, DateOnly today, CancellationToken ct)
    {
        var ids = itemIds.Distinct().ToList();
        var batches = await db.Set<StockBatch>().AsNoTracking()
            .Where(b => b.StoreId == storeId && ids.Contains(b.ItemId) && b.QuantityOnHand > 0)
            .Select(b => new { b.ItemId, b.QuantityOnHand, b.QuantityReserved, b.ExpiryDate, b.IsBlocked, b.Mrp, b.ReceivedOn })
            .ToListAsync(ct);

        return ids.ToDictionary(id => id, id =>
        {
            var mine = batches.Where(b => b.ItemId == id).ToList();
            var usable = mine
                .Where(b => !b.IsBlocked && b.QuantityOnHand > b.QuantityReserved && (b.ExpiryDate is null || b.ExpiryDate >= today))
                .OrderBy(b => b.ExpiryDate is null)
                .ThenBy(b => b.ExpiryDate)
                .ThenBy(b => b.ReceivedOn)
                .ToList();
            return new StockAvailability(
                id,
                mine.Sum(b => b.QuantityOnHand),
                usable.Sum(b => b.QuantityOnHand - b.QuantityReserved),
                usable.Select(b => b.ExpiryDate).FirstOrDefault(),
                usable.Count == 0 ? null : usable[0].Mrp);
        });
    }

    /// <summary>Batches with stock that expire within <paramref name="withinDays"/> (and optionally those already expired).</summary>
    public static async Task<List<ExpiringBatch>> ExpiringAsync(
        HospitalDbContext db, IReadOnlyCollection<Guid>? storeIds, DateOnly today, int withinDays, bool includeExpired, CancellationToken ct)
    {
        var limit = today.AddDays(withinDays);
        var query =
            from b in db.Set<StockBatch>().AsNoTracking()
            join s in db.Set<Store>() on b.StoreId equals s.Id
            join i in db.Set<Item>() on b.ItemId equals i.Id
            where b.QuantityOnHand > 0 && b.ExpiryDate != null && b.ExpiryDate <= limit && (includeExpired || b.ExpiryDate >= today)
            select new { b.Id, b.StoreId, StoreName = s.Name, b.ItemId, i.Code, i.Name, b.BatchNumber, b.ExpiryDate, b.QuantityOnHand, b.PurchaseRate, b.Mrp };
        if (storeIds is not null)
        {
            var stores = storeIds.ToList();
            query = query.Where(x => stores.Contains(x.StoreId));
        }

        var rows = await query.OrderBy(x => x.ExpiryDate).ThenBy(x => x.Name).ToListAsync(ct);
        return rows.Select(x => new ExpiringBatch(
                x.Id, x.StoreId, x.StoreName, x.ItemId, x.Code, x.Name, x.BatchNumber, x.ExpiryDate!.Value, x.ExpiryDate.Value.DayNumber - today.DayNumber,
                x.QuantityOnHand, Money.Round(x.QuantityOnHand * x.PurchaseRate), Money.Round(x.QuantityOnHand * x.Mrp)))
            .ToList();
    }
}
