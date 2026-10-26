using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Linen;

namespace HyFib.HealthCare360.Modules.SupportServices.Seed;

/// <summary>
/// Idempotent reference data (checked by code on every start): diet-charge and ambulance services in the service catalogue, the diet
/// master, and — for the default facility — common instrument sets, a canteen menu and a linen list. These are starting masters, not
/// demo transactions, so they are seeded in every installation.
/// </summary>
internal sealed class SupportServicesSeeder : ISeeder
{
    public int Order => 170;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        await SeedDietServicesAsync(db, cancellationToken);
        await SeedDietTypesAsync(db, cancellationToken);
        await SeedAmbulanceServicesAsync(db, cancellationToken);

        var facility = await db.Set<Facility>().OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (facility is null)
        {
            return;
        }

        await SeedInstrumentSetsAsync(db, facility.Id, cancellationToken);
        await SeedMenuAsync(db, facility.Id, cancellationToken);
        await SeedLinenAsync(db, facility.Id, cancellationToken);
    }

    private static async Task SeedDietServicesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<ServiceItem>().Where(s => s.Code.StartsWith("DIET-")).Select(s => s.Code).ToListAsync(ct);
        var dietDepartment = await db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => d.Code == "DIET").OrderBy(d => d.CreatedAt).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        foreach (var service in SeedCatalog.DietServices.Where(s => !existing.Contains(s.Code)))
        {
            db.Add(new ServiceItem
            {
                Code = service.Code,
                Name = service.Name,
                Category = ServiceCategory.Dietary,
                BasePrice = service.Price,
                DepartmentId = dietDepartment,
                SacCode = "999312",
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedAmbulanceServicesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var codes = SeedCatalog.AmbulanceServices.Select(s => s.Code).ToList();
        var existing = await db.Set<ServiceItem>().Where(s => codes.Contains(s.Code)).Select(s => s.Code).ToListAsync(ct);
        foreach (var service in SeedCatalog.AmbulanceServices.Where(s => !existing.Contains(s.Code)))
        {
            // SAC 999315: ambulance services, exempt from GST (Notification 12/2017-CT(R), entry 74).
            db.Add(new ServiceItem { Code = service.Code, Name = service.Name, Category = ServiceCategory.Ambulance, BasePrice = service.Price, SacCode = "999315" });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedLinenAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        var existing = await db.Set<LinenItem>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(i => i.FacilityId == facilityId)
            .Select(i => i.Code).ToListAsync(ct);
        foreach (var (code, name, life) in SeedCatalog.Linen.Where(l => !existing.Contains(l.Code)))
        {
            db.Add(new LinenItem { FacilityId = facilityId, Code = code, Name = name, LifeWashes = life });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedDietTypesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<DietType>().Select(d => d.Code).ToListAsync(ct);
        var services = await db.Set<ServiceItem>().Where(s => s.Code.StartsWith("DIET-")).ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        var order = 0;
        foreach (var diet in SeedCatalog.Diets)
        {
            order += 10;
            if (existing.Contains(diet.Code))
            {
                continue;
            }

            db.Add(new DietType
            {
                Code = diet.Code,
                Name = diet.Name,
                Description = diet.Description,
                Category = diet.Category,
                ChargeServiceItemId = diet.ChargeCode is not null && services.TryGetValue(diet.ChargeCode, out var serviceId) ? serviceId : null,
                DefaultMealSlots = diet.Slots,
                DisplayOrder = order,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedInstrumentSetsAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        var existing = await db.Set<InstrumentSet>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(s => s.FacilityId == facilityId).Select(s => s.Code).ToListAsync(ct);
        var departments = await db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => d.FacilityId == facilityId).ToDictionaryAsync(d => d.Code, d => d.Id, ct);
        foreach (var seed in SeedCatalog.InstrumentSets.Where(s => !existing.Contains(s.Code)))
        {
            var set = new InstrumentSet
            {
                FacilityId = facilityId,
                Code = seed.Code,
                Name = seed.Name,
                Category = seed.Category,
                OwnerDepartmentId = seed.Department is not null && departments.TryGetValue(seed.Department, out var departmentId) ? departmentId : null,
                Method = seed.Method,
                ShelfLifeDays = seed.ShelfLifeDays,
            };
            set.ReplaceItems(seed.Items.Select(i => new CountSheetLine(i.Name, i.Quantity)));
            db.Add(set);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedMenuAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        var existing = await db.Set<MenuItem>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(m => m.FacilityId == facilityId).Select(m => m.Code).ToListAsync(ct);
        var order = 0;
        foreach (var item in SeedCatalog.Menu)
        {
            order += 10;
            if (existing.Contains(item.Code))
            {
                continue;
            }

            db.Add(new MenuItem
            {
                FacilityId = facilityId,
                Code = item.Code,
                Name = item.Name,
                Category = item.Category,
                Price = item.Price,
                GstRate = item.GstRate,
                HsnSac = item.HsnSac,
                PriceIncludesGst = true,
                IsVeg = item.IsVeg,
                TrackStock = item.TrackStock,
                StockOnHand = item.TrackStock ? 100 : 0,
                DisplayOrder = order,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
