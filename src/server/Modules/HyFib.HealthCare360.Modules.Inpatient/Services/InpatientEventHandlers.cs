using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

// Handlers run inside the raising module's unit of work (before commit): they only stage changes, never save, and never
// throw for data they do not recognise, so an in-patient concern can never block a pharmacy or registration transaction.

/// <summary>Signed in-patient e-prescriptions become lines on the medication chart.</summary>
internal sealed class PrescriptionSignedHandler(MarService mar) : IDomainEventHandler<PrescriptionSigned>
{
    public Task HandleAsync(PrescriptionSigned domainEvent, CancellationToken cancellationToken) => mar.ChartPrescriptionAsync(domainEvent, cancellationToken);
}

/// <summary>A cancelled prescription stops its chart lines and cancels doses not yet due.</summary>
internal sealed class PrescriptionCancelledHandler(MarService mar) : IDomainEventHandler<PrescriptionCancelled>
{
    public Task HandleAsync(PrescriptionCancelled domainEvent, CancellationToken cancellationToken) => mar.StopPrescriptionAsync(domainEvent, cancellationToken);
}

/// <summary>Medication issued by the IP pharmacy is scheduled on the MAR (PH-03 MAR feed).</summary>
internal sealed class MedicationDispensedHandler(MarService mar) : IDomainEventHandler<MedicationDispensed>
{
    public Task HandleAsync(MedicationDispensed domainEvent, CancellationToken cancellationToken) => mar.ChartDispenseAsync(domainEvent, cancellationToken);
}

/// <summary>
/// Duplicate UHIDs merged by the MRD: this module re-points its own records to the surviving patient in the same
/// transaction as the merge, across facilities (the facility filter is ignored on purpose).
/// </summary>
internal sealed class PatientsMergedHandler(HospitalDbContext db) : IDomainEventHandler<PatientsMerged>
{
    public async Task HandleAsync(PatientsMerged domainEvent, CancellationToken cancellationToken)
    {
        var from = domainEvent.SourcePatientId;
        var to = domainEvent.TargetPatientId;
        await RepointAsync<Admission>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<NursingNote>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<ShiftHandover>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<IntakeOutputEntry>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<NursingAssessment>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<MedicationOrder>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<MedicationAdministration>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<ConsultantVisit>(x => x.PatientId == from, x => x.PatientId = to, cancellationToken);
        await RepointAsync<Bed>(x => x.ReservedForPatientId == from, x => x.ReservedForPatientId = to, cancellationToken);
    }

    private async Task RepointAsync<T>(Expression<Func<T, bool>> predicate, Action<T> repoint, CancellationToken ct)
        where T : class
    {
        var rows = await db.Set<T>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(predicate).ToListAsync(ct);
        rows.ForEach(repoint);
    }
}
