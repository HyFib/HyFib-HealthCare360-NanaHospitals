using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// The theatre's completed operations and the blood bank's bedside transfusions and reactions, as raw facts for the
/// Quality module's indicators (unplanned return to OT, SSI rate, transfusion reaction rate) and its infection
/// surveillance. Reads across facilities only by the facility asked for.
/// </summary>
internal sealed class SurgeryAndTransfusionFacts(HospitalDbContext db) : ISurgeryAndTransfusionFacts
{
    public bool Available => true;

    public async Task<IReadOnlyList<SurgeryFact>> SurgeriesAsync(Guid facilityId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        await Completed().Where(s => s.FacilityId == facilityId && s.CompletedAt >= start && s.CompletedAt < end)
            .Select(s => new SurgeryFact(s.Id, s.BookingNumber, s.PatientId, s.AdmissionId, s.ProcedureName, s.Priority == SurgeryPriority.Elective,
                s.StartedAt ?? s.CompletedAt!.Value, s.CompletedAt!.Value))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SurgeryFact>> PatientSurgeriesAsync(Guid patientId, DateTimeOffset since, CancellationToken cancellationToken) =>
        await Completed().Where(s => s.PatientId == patientId && s.CompletedAt >= since)
            .OrderByDescending(s => s.CompletedAt)
            .Select(s => new SurgeryFact(s.Id, s.BookingNumber, s.PatientId, s.AdmissionId, s.ProcedureName, s.Priority == SurgeryPriority.Elective,
                s.StartedAt ?? s.CompletedAt!.Value, s.CompletedAt!.Value))
            .Take(50)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TransfusionFact>> TransfusionsAsync(Guid facilityId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        await db.Set<Transfusion>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(t => t.FacilityId == facilityId && t.StartedAt >= start && t.StartedAt < end)
            .Select(t => new TransfusionFact(t.Id, t.UnitNumber, t.PatientId, t.StartedAt))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TransfusionReactionFact>> TransfusionReactionsAsync(Guid facilityId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken) =>
        (await db.Set<TransfusionReaction>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.FacilityId == facilityId && r.OnsetAt >= start && r.OnsetAt < end)
            .Select(r => new { r.Id, r.UnitNumber, r.PatientId, r.OnsetAt, r.Type, r.Severity })
            .ToListAsync(cancellationToken))
        .Select(r => new TransfusionReactionFact(r.Id, r.UnitNumber, r.PatientId, r.OnsetAt, r.Type.ToString(), r.Severity.ToString()))
        .ToList();

    private IQueryable<SurgeryBooking> Completed() =>
        db.Set<SurgeryBooking>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(s => s.Status == SurgeryStatus.Completed && s.CompletedAt != null);
}
