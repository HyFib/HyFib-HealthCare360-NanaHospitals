using CapaEntity = HyFib.HealthCare360.Modules.Quality.Domain.Capa;

namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>The CAPA register as other modules link it (<see cref="ICapaRegister"/>); facility-scoped like every CAPA read.</summary>
internal sealed class CapaRegister(HospitalDbContext db) : ICapaRegister
{
    public async Task<CapaLink?> FindAsync(Guid capaId, CancellationToken cancellationToken) =>
        (await FindManyAsync([capaId], cancellationToken)).GetValueOrDefault(capaId);

    public async Task<CapaLink?> FindByNumberAsync(string number, CancellationToken cancellationToken)
    {
        var trimmed = number.Trim().ToUpperInvariant();
        var id = await db.Set<CapaEntity>().AsNoTracking().Where(c => c.CapaNumber == trimmed).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken);
        return id is { } found ? await FindAsync(found, cancellationToken) : null;
    }

    public async Task<IReadOnlyDictionary<Guid, CapaLink>> FindManyAsync(IReadOnlyCollection<Guid> capaIds, CancellationToken cancellationToken)
    {
        if (capaIds.Count == 0)
        {
            return new Dictionary<Guid, CapaLink>();
        }

        var ids = capaIds.Distinct().ToList();
        var found = await db.Set<CapaEntity>().AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.CapaNumber, c.Status, c.DueDate })
            .ToListAsync(cancellationToken);
        return found.ToDictionary(
            c => c.Id,
            c => new CapaLink(c.Id, c.CapaNumber, c.Status.ToString(), c.Status is Domain.CapaStatus.Open or Domain.CapaStatus.InProgress, c.DueDate));
    }
}
