using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// The bed charges <see cref="InpatientChargeService"/> posts by itself, day after day: a ward's bed charge, which each new
/// bed assignment takes, and the bed charge a stay in progress took with its bed, which it keeps until it moves bed or
/// ends, whatever its ward is re-pointed to. Taken out of use, billing would refuse those days and they would go
/// unbilled, so the services master refuses it while either charges by it.
/// </summary>
/// <remarks>The nursing day and the consultant's visit are looked up by code and charged only while in use: not here.</remarks>
internal sealed class InpatientServiceCharges(HospitalDbContext db) : IServiceChargeSource
{
    /// <summary>The stays named in the refusal; the rest are counted.</summary>
    private const int Named = 5;

    public async Task<IReadOnlyList<string>> UsesAsync(Guid serviceItemId, CancellationToken cancellationToken)
    {
        List<string> uses = [];
        var wards = await db.Set<Ward>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(w => w.BedServiceItemId == serviceItemId && w.IsActive)
            .OrderBy(w => w.Name)
            .Select(w => w.Name)
            .ToListAsync(cancellationToken);
        uses.AddRange(wards.Select(w => $"the ward {w} charges its beds' days by it"));

        var stays = (await (
                    from b in db.Set<BedAssignment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                    join a in db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking() on b.AdmissionId equals a.Id
                    where b.BedServiceItemId == serviceItemId && b.ToAt == null
                        && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated)
                    select new { a.AdmissionNumber, a.AdmittedAt })
                .Distinct()
                .ToListAsync(cancellationToken))
            .OrderBy(s => s.AdmittedAt)
            .Select(s => s.AdmissionNumber)
            .ToList();
        if (stays.Count > 0)
        {
            uses.Add($"{stays.Count} stay(s) in progress are in beds charged by it, and keep that charge until they move bed or end " +
                $"({string.Join(", ", stays.Take(Named))}{(stays.Count > Named ? ", …" : string.Empty)})");
        }

        return uses;
    }
}
