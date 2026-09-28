using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.SupplyChain.Seed;

/// <summary>
/// Reference data (idempotent, checked by code): item categories, the item catalogue and the standard stores of
/// the first facility. Demo vendors, opening stock and assets are added only when <c>HealthCare360:SeedDemoData</c>
/// is true and the tables are still empty.
/// </summary>
internal sealed class SupplyChainSeeder(IOptions<PlatformOptions> options, StockService stock, INumberSeries numbers, TimeProvider time) : ISeeder
{
    public int Order => 150;

    private static readonly (string Code, string Name, StoreType Type, string Department, string Location)[] Stores =
    [
        ("CS", "Central Store", StoreType.Central, "STORE", "Basement, Block A"),
        ("OPPH", "OP Pharmacy", StoreType.Pharmacy, "PHARM", "Ground floor, near OPD"),
        ("IPPH", "IP Pharmacy", StoreType.Pharmacy, "PHARM", "First floor, Block B"),
        ("ICUS", "ICU Store", StoreType.Ward, "ICU", "ICU, second floor"),
        ("OTS", "OT Store", StoreType.OT, "OT", "OT complex, third floor"),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        await SeedCategoriesAsync(db, cancellationToken);
        await SeedItemsAsync(db, cancellationToken);

        var facility = await db.Set<Facility>().OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (facility is null)
        {
            return;
        }

        await SeedStoresAsync(db, facility, cancellationToken);
        if (options.Value.SeedDemoData)
        {
            await DemoInventorySeed.SeedAsync(db, facility, stock, numbers, time, cancellationToken);
        }
    }

    private static async Task SeedCategoriesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<ItemCategory>().Select(c => c.Code).ToListAsync(ct);
        foreach (var (code, name, type) in ItemCatalogSeed.Categories.Where(c => !existing.Contains(c.Code)))
        {
            db.Add(new ItemCategory { Code = code, Name = name, DefaultType = type });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedItemsAsync(HospitalDbContext db, CancellationToken ct)
    {
        var categories = await db.Set<ItemCategory>().ToDictionaryAsync(c => c.Code, c => c.Id, ct);
        var existing = await db.Set<Item>().Select(i => i.Code).ToListAsync(ct);
        foreach (var seed in ItemCatalogSeed.Items.Where(i => !existing.Contains(i.Code)))
        {
            db.Add(new Item
            {
                Code = seed.Code,
                Name = seed.Name,
                GenericName = seed.Generic,
                Type = seed.Type,
                CategoryId = categories.TryGetValue(seed.Category, out var categoryId) ? categoryId : null,
                Strength = seed.Strength,
                DosageForm = seed.Form,
                PackSize = seed.PackSize,
                BaseUnit = seed.BaseUnit,
                PurchaseUnit = seed.PurchaseUnit,
                PurchaseUnitFactor = seed.Factor,
                HsnCode = seed.Hsn,
                GstRate = seed.Gst,
                Schedule = seed.Schedule,
                IsControlled = seed.Controlled,
                IsColdChain = seed.ColdChain,
                VedClass = seed.Ved,
                ReorderLevel = seed.ReorderLevel,
                ReorderQuantity = seed.ReorderQuantity,
                MinimumStock = Math.Round(seed.ReorderLevel / 2, 0),
                MaximumStock = seed.ReorderLevel + seed.ReorderQuantity,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedStoresAsync(HospitalDbContext db, Facility facility, CancellationToken ct)
    {
        var departments = await db.Set<Department>().Where(d => d.FacilityId == facility.Id).ToDictionaryAsync(d => d.Code, d => d.Id, ct);
        var existing = await db.Set<Store>().Where(s => s.FacilityId == facility.Id).Select(s => s.Code).ToListAsync(ct);
        foreach (var (code, name, type, department, location) in Stores.Where(s => !existing.Contains(s.Code)))
        {
            db.Add(new Store
            {
                FacilityId = facility.Id,
                Code = code,
                Name = name,
                Type = type,
                DepartmentId = departments.TryGetValue(department, out var departmentId) ? departmentId : null,
                Location = location,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
