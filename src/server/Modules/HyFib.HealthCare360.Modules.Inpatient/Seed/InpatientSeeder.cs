using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Inpatient.Seed;

/// <summary>
/// Demonstration wards, rooms and beds (only when <c>HealthCare360:SeedDemoData</c> is true), linked to the bed-class
/// services the platform seeds (BED-GEN, BED-SEMI, BED-PVT, BED-DLX, BED-ICU, BED-NICU). Idempotent by ward code.
/// </summary>
internal sealed class InpatientSeeder(IOptions<PlatformOptions> options) : ISeeder
{
    public int Order => 160;

    private sealed record DemoWard(
        string Code, string Name, WardType Type, WardGenderPolicy Gender, string Floor, string Service, int Beds, int BedsPerRoom, string? Department, bool CriticalCare);

    private static readonly DemoWard[] Wards =
    [
        new("GWM", "General Ward — Male", WardType.General, WardGenderPolicy.MaleOnly, "1", "BED-GEN", 12, 0, "GM", false),
        new("GWF", "General Ward — Female", WardType.General, WardGenderPolicy.FemaleOnly, "1", "BED-GEN", 12, 0, "GM", false),
        new("SPW", "Semi-Private Ward", WardType.SemiPrivate, WardGenderPolicy.Any, "2", "BED-SEMI", 8, 2, null, false),
        new("PVT", "Private Rooms", WardType.Private, WardGenderPolicy.Any, "3", "BED-PVT", 8, 1, null, false),
        new("DLX", "Deluxe Suites", WardType.Deluxe, WardGenderPolicy.Any, "4", "BED-DLX", 4, 1, null, false),
        new("ICU", "Intensive Care Unit", WardType.Icu, WardGenderPolicy.Any, "2", "BED-ICU", 8, 0, "ICU", true),
        new("NICU", "Neonatal ICU", WardType.Nicu, WardGenderPolicy.Any, "2", "BED-NICU", 6, 0, "PED", true),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        if (!options.Value.SeedDemoData)
        {
            return;
        }

        var facility = await db.Set<Facility>().OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (facility is null)
        {
            return;
        }

        var existing = await db.Set<Ward>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(w => w.FacilityId == facility.Id)
            .Select(w => w.Code)
            .ToListAsync(cancellationToken);
        var services = await db.Set<ServiceItem>()
            .Where(s => s.Code.StartsWith("BED-"))
            .ToDictionaryAsync(s => s.Code, s => s.Id, cancellationToken);
        var departments = await db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => d.FacilityId == facility.Id)
            .ToDictionaryAsync(d => d.Code, d => d.Id, cancellationToken);

        var order = 0;
        foreach (var demo in Wards)
        {
            order += 10;
            if (existing.Contains(demo.Code))
            {
                continue;
            }

            var ward = new Ward
            {
                FacilityId = facility.Id,
                Code = demo.Code,
                Name = demo.Name,
                Type = demo.Type,
                Floor = demo.Floor,
                GenderPolicy = demo.Gender,
                NurseStation = $"NS-{demo.Code}",
                DepartmentId = demo.Department is not null && departments.TryGetValue(demo.Department, out var departmentId) ? departmentId : null,
                BedServiceItemId = services.TryGetValue(demo.Service, out var serviceId) ? serviceId : null,
                DisplayOrder = order,
            };
            db.Add(ward);

            Room? room = null;
            for (var n = 1; n <= demo.Beds; n++)
            {
                if (demo.BedsPerRoom > 0 && (n - 1) % demo.BedsPerRoom == 0)
                {
                    room = new Room
                    {
                        FacilityId = facility.Id,
                        WardId = ward.Id,
                        Number = $"{demo.Floor}{((n - 1) / demo.BedsPerRoom) + 1:00}",
                    };
                    db.Add(room);
                }

                db.Add(new Bed
                {
                    FacilityId = facility.Id,
                    WardId = ward.Id,
                    RoomId = room?.Id,
                    Label = $"{demo.Code}-{n:00}",
                    Status = BedStatus.Vacant,
                    HasOxygen = demo.CriticalCare || demo.Type != WardType.General || n % 2 == 1,
                    HasMonitor = demo.CriticalCare,
                    HasVentilator = demo.CriticalCare && n <= demo.Beds / 2,
                    DisplayOrder = n * 10,
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
