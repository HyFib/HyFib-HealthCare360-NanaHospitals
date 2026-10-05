using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>The active facilities, by id and code, so each modality on the allow-list can name the facility it serves.</summary>
internal interface IWorklistFacilities
{
    Task<IReadOnlyList<(Guid Id, string Code)>> ActiveAsync(CancellationToken cancellationToken);
}

internal sealed class WorklistFacilities(HospitalDbContext db) : IWorklistFacilities
{
    public async Task<IReadOnlyList<(Guid Id, string Code)>> ActiveAsync(CancellationToken cancellationToken) =>
        (await db.Set<Facility>().AsNoTracking().Where(f => f.IsActive).Select(f => new { f.Id, f.Code }).ToListAsync(cancellationToken))
        .Select(f => (f.Id, f.Code))
        .ToList();
}
