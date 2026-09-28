using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>The ids a page of resources refers to, collected before the display names are loaded.</summary>
internal sealed class ReferenceIds
{
    public HashSet<Guid> Practitioners { get; } = [];
    public HashSet<Guid> Departments { get; } = [];
    public HashSet<Guid> Facilities { get; } = [];
    public HashSet<Guid> Admissions { get; } = [];

    public ReferenceIds Practitioner(params Guid?[] ids) => Add(Practitioners, ids);

    public ReferenceIds Department(params Guid?[] ids) => Add(Departments, ids);

    public ReferenceIds Facility(params Guid?[] ids) => Add(Facilities, ids);

    public ReferenceIds Admission(params Guid?[] ids) => Add(Admissions, ids);

    private ReferenceIds Add(HashSet<Guid> set, Guid?[] ids)
    {
        foreach (var id in ids)
        {
            if (id is { } value && value != Guid.Empty)
            {
                set.Add(value);
            }
        }

        return this;
    }
}

/// <summary>
/// Loads practitioner, department and facility names (and admission → visit links) for one page in a few small
/// queries. Deleted masters still resolve, so historical records keep their doctor's name.
/// </summary>
internal sealed class ReferenceDirectoryLoader(HospitalDbContext db)
{
    public async Task<ReferenceDirectory> LoadAsync(ReferenceIds ids, CancellationToken ct)
    {
        var practitionerIds = ids.Practitioners.ToList();
        var departmentIds = ids.Departments.ToList();
        var facilityIds = ids.Facilities.ToList();
        var admissionIds = ids.Admissions.ToList();

        var practitioners = practitionerIds.Count == 0
            ? []
            : await db.Set<Practitioner>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(p => practitionerIds.Contains(p.Id))
                .Select(p => new { p.Id, p.DisplayName })
                .ToListAsync(ct);

        var departments = departmentIds.Count == 0
            ? []
            : await db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(d => departmentIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Name })
                .ToListAsync(ct);

        var facilities = facilityIds.Count == 0
            ? []
            : await db.Set<Facility>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(f => facilityIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Name, f.HfrId })
                .ToListAsync(ct);

        var admissions = admissionIds.Count == 0
            ? []
            : await db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(a => admissionIds.Contains(a.Id))
                .Select(a => new { a.Id, a.VisitId })
                .ToListAsync(ct);

        return new ReferenceDirectory
        {
            Practitioners = practitioners.ToDictionary(p => p.Id, p => p.DisplayName),
            Departments = departments.ToDictionary(d => d.Id, d => d.Name),
            Facilities = facilities.ToDictionary(f => f.Id, f => new FacilityInfo(f.Name, f.HfrId)),
            AdmissionVisits = admissions.ToDictionary(a => a.Id, a => a.VisitId),
        };
    }
}
