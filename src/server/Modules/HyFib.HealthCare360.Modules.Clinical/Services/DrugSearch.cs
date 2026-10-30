using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The drug master as prescribers see it (ERX-01): active items of type Drug from the SupplyChain item master, searched
/// by code, barcode, name, generic or brand (<see cref="InventoryQueries.Search"/>), with the stock of the facility's
/// pharmacy stores (<see cref="InventoryQueries.AvailabilityAsync"/>). Read-only; prescribers need no inventory
/// permission for it.
/// </summary>
public static class DrugSearch
{
    public const int DefaultTake = 20;
    public const int MaxTake = 50;

    /// <summary>Rows to return: 20 by default, at least 1 and never more than 50.</summary>
    public static int Take(int? take) => Math.Clamp(take ?? DefaultTake, 1, MaxTake);

    public static IQueryable<ItemSummary> Query(IQueryable<Item> items, string? search, int? take) =>
        items.Where(i => i.IsActive && i.Type == ItemType.Drug)
            .Search(search)
            .OrderBy(i => i.Name)
            .ThenBy(i => i.Code)
            .Take(Take(take))
            .Select(i => new ItemSummary(i.Id, i.Code, i.Name, i.GenericName, i.BrandName, i.Strength, i.DosageForm, i.Type, i.Schedule,
                i.IsControlled, i.IsColdChain, i.BaseUnit, i.HsnCode, i.GstRate, i.IsActive, i.Manufacturer, i.Specification, i.CeilingPrice));

    /// <summary>
    /// Stock per item over the facility's active pharmacy stores; null when the facility has none (availability unknown).
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, PharmacyStock>?> PharmacyStockAsync(
        HospitalDbContext db, Guid facilityId, IReadOnlyCollection<Guid> itemIds, DateOnly today, CancellationToken ct)
    {
        var stores = await db.Set<Store>().AsNoTracking()
            .Where(s => s.FacilityId == facilityId && s.Type == StoreType.Pharmacy && s.IsActive)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (stores.Count == 0)
        {
            return null;
        }

        var perStore = new List<Dictionary<Guid, StockAvailability>>(stores.Count);
        foreach (var store in stores)
        {
            perStore.Add(await InventoryQueries.AvailabilityAsync(db, store, itemIds, today, ct));
        }

        return itemIds.Distinct().ToDictionary(id => id, id => Combine(perStore.Select(s => s.GetValueOrDefault(id)).OfType<StockAvailability>()));
    }

    /// <summary>Sellable quantity summed over the stores and the nearest usable expiry among them.</summary>
    public static PharmacyStock Combine(IEnumerable<StockAvailability> perStore)
    {
        var positions = perStore.ToList();
        return new PharmacyStock(positions.Sum(p => p.Available), positions.Select(p => p.NearestExpiry).Min());
    }

    public static PrescribableDrug ToDrug(ItemSummary item, PharmacyStock? stock)
    {
        var controlled = item.RequiresRegister;
        return new PrescribableDrug(
            item.Id,
            item.Code,
            item.Name,
            item.GenericName,
            item.BrandName,
            item.Strength,
            item.DosageForm,
            item.Schedule,
            controlled,
            controlled ? (item.Schedule == DrugSchedule.X ? "X" : "H1") : null,
            item.RequiresPrescription,
            item.IsColdChain,
            item.BaseUnit,
            stock?.Available,
            stock?.NearestExpiry);
    }
}
