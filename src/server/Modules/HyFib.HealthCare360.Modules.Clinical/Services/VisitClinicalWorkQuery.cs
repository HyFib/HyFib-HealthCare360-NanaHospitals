using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The clinical work on a visit, for the desk deciding whether its cancellation waits for a second person: the
/// encounters, their notes, the order lines and e-prescriptions not cancelled, and the doctors who did them. Read across
/// facilities: the visit is the desk's, the record the clinicians'.
/// </summary>
internal sealed class VisitClinicalWorkQuery(HospitalDbContext db) : IVisitClinicalWork
{
    public async Task<VisitClinicalWork> OfVisitAsync(Guid visitId, CancellationToken ct)
    {
        Guid? visit = visitId;
        var encounters = await db.Set<Encounter>().AsNoTracking()
            .Where(e => e.VisitId == visit)
            .Select(e => new { e.Id, e.PractitionerId, Signed = e.SignedAt != null })
            .ToListAsync(ct);
        var encounterIds = encounters.Select(e => (Guid?)e.Id).ToList();
        var notes = encounterIds.Count == 0 ? 0 : await db.Set<ClinicalNote>().AsNoTracking().CountAsync(n => encounterIds.Contains(n.EncounterId), ct);
        var lines = await db.Set<ClinicalOrder>().AsNoTracking()
            .Where(o => o.VisitId == visit)
            .SelectMany(o => o.Items, (o, i) => new { o.OrderingPractitionerId, i.Status })
            .Where(l => l.Status != OrderItemStatus.Cancelled)
            .ToListAsync(ct);
        var prescriptions = await db.Set<Prescription>().AsNoTracking()
            .Where(p => p.VisitId == visit && p.Status != PrescriptionStatus.Cancelled)
            .Select(p => p.PractitionerId)
            .ToListAsync(ct);
        var doctors = encounters.Select(e => e.PractitionerId)
            .Concat(lines.Where(l => l.OrderingPractitionerId is not null).Select(l => l.OrderingPractitionerId!.Value))
            .Concat(prescriptions)
            .Distinct()
            .ToList();
        return new VisitClinicalWork(encounters.Count, encounters.Count(e => e.Signed), notes, lines.Count, prescriptions.Count, doctors);
    }
}
