using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>An item of the OT store as the picker lists it.</summary>
internal sealed record OtStoreCandidate(Guid Id, string Code, string Name, string? GenericName, string? Manufacturer, ItemType Type, string BaseUnit);

/// <summary>A batch with stock in the OT store (quantities in the item's base unit).</summary>
internal sealed record OtStoreBatchRow(
    Guid BatchId, Guid ItemId, string BatchNumber, DateOnly? ExpiryDate, decimal OnHand, decimal Reserved, bool IsBlocked, decimal Mrp, DateOnly ReceivedOn);

/// <summary>
/// What OT staff may pick in the implants and consumables dialog: items with issuable stock in the facility's OT store
/// (the store <see cref="OtClinicalService"/> issues from), with the available quantity and the batches FEFO would issue
/// first. OT staff hold no inventory permissions, so no cost or vendor data is exposed.
/// </summary>
internal static class OtStoreLookup
{
    public const int DefaultTake = 20;
    public const int MaxTake = 50;

    /// <summary>Batches listed per item (the lot on the pack is usually one of the first to expire).</summary>
    public const int BatchesPerItem = 3;

    public static int Take(int? take) => Math.Clamp(take ?? DefaultTake, 1, MaxTake);

    /// <summary>Active items with at least one issuable batch in the store, by name; searched like the inventory counters.</summary>
    public static IQueryable<OtStoreCandidate> Candidates(
        IQueryable<Item> items, IQueryable<StockBatch> batches, Guid storeId, DateOnly today, string? search, int? take)
    {
        var inStock = batches
            .Where(b => b.StoreId == storeId && b.QuantityOnHand > b.QuantityReserved && !b.IsBlocked && (b.ExpiryDate == null || b.ExpiryDate >= today))
            .Select(b => b.ItemId);
        return items.Where(i => i.IsActive && inStock.Contains(i.Id))
            .Search(search)
            .OrderBy(i => i.Name)
            .ThenBy(i => i.Code)
            .Take(Take(take))
            .Select(i => new OtStoreCandidate(i.Id, i.Code, i.Name, i.GenericName, i.Manufacturer, i.Type, i.BaseUnit));
    }

    public static IQueryable<OtStoreBatchRow> Batches(IQueryable<StockBatch> batches, Guid storeId, List<Guid> itemIds) =>
        batches.Where(b => b.StoreId == storeId && itemIds.Contains(b.ItemId) && b.QuantityOnHand > 0)
            .Select(b => new OtStoreBatchRow(b.Id, b.ItemId, b.BatchNumber, b.ExpiryDate, b.QuantityOnHand, b.QuantityReserved, b.IsBlocked, b.Mrp, b.ReceivedOn));

    /// <summary>Issuable now: not blocked, not expired, and not all of it reserved for another case.</summary>
    public static bool IsIssuable(OtStoreBatchRow batch, DateOnly today) =>
        !batch.IsBlocked && batch.OnHand > batch.Reserved && (batch.ExpiryDate is null || batch.ExpiryDate >= today);

    /// <summary>Available quantity, nearest expiry, FEFO MRP and the first <see cref="BatchesPerItem"/> issuable batches (FEFO order).</summary>
    public static OtStoreItem ToItem(OtStoreCandidate item, IEnumerable<OtStoreBatchRow> batches, DateOnly today)
    {
        var usable = batches
            .Where(b => b.ItemId == item.Id && IsIssuable(b, today))
            .OrderBy(b => b.ExpiryDate is null)
            .ThenBy(b => b.ExpiryDate)
            .ThenBy(b => b.ReceivedOn)
            .ToList();
        return new OtStoreItem(
            item.Id,
            item.Code,
            item.Name,
            item.GenericName,
            item.Manufacturer,
            item.Type,
            item.BaseUnit,
            usable.Sum(b => b.OnHand - b.Reserved),
            usable.Count == 0 ? null : usable[0].ExpiryDate,
            usable.Count == 0 ? null : usable[0].Mrp,
            usable.Take(BatchesPerItem).Select(b => new OtStoreBatch(b.BatchId, b.BatchNumber, b.ExpiryDate, b.OnHand - b.Reserved, b.Mrp)).ToList());
    }
}
