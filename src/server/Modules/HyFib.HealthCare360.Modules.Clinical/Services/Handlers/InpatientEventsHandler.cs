using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Handlers;

/// <summary>
/// Opens the MRD chart when a patient is admitted and, at discharge, sets its completion due date, queues the admission
/// for ICD coding (MRD-02/03) and stamps the discharge time on the discharge summary. Runs in the Inpatient transaction.
/// </summary>
internal sealed class InpatientEventsHandler(HospitalDbContext db, MedicalRecordService charts, CodingService coding)
    : IDomainEventHandler<PatientAdmitted>, IDomainEventHandler<PatientDischarged>
{
    public async Task HandleAsync(PatientAdmitted domainEvent, CancellationToken cancellationToken)
    {
        var chart = await charts.EnsureForAdmissionAsync(domainEvent.AdmissionId, domainEvent.PatientId, domainEvent.FacilityId, domainEvent.VisitId,
            domainEvent.AdmittingPractitionerId, cancellationToken);
        chart.AdmittedAt ??= domainEvent.AdmittedAt;
    }

    public async Task HandleAsync(PatientDischarged domainEvent, CancellationToken cancellationToken)
    {
        var facilityId = await charts.Charts().Where(m => m.AdmissionId == domainEvent.AdmissionId).Select(m => (Guid?)m.FacilityId).FirstOrDefaultAsync(cancellationToken)
                         ?? await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                             .Where(v => v.Id == domainEvent.VisitId).Select(v => (Guid?)v.FacilityId).FirstOrDefaultAsync(cancellationToken)
                         ?? Guid.Empty;
        var dischargeDate = ClinicalClock.LocalDate(domainEvent.DischargedAt);

        var chart = await charts.EnsureForAdmissionAsync(domainEvent.AdmissionId, domainEvent.PatientId, facilityId, domainEvent.VisitId, null, cancellationToken);
        chart.DischargedAt = domainEvent.DischargedAt;
        chart.DueDate ??= dischargeDate.AddDays(MedicalRecordService.ChartCompletionDays);

        await coding.EnsureForAdmissionAsync(domainEvent.AdmissionId, domainEvent.PatientId, facilityId, domainEvent.VisitId, dischargeDate, cancellationToken);

        var summary = await db.Set<DischargeSummary>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(s => s.AdmissionId == domainEvent.AdmissionId, cancellationToken);
        if (summary is not null)
        {
            summary.DischargedAt ??= domainEvent.DischargedAt;
        }
    }
}
