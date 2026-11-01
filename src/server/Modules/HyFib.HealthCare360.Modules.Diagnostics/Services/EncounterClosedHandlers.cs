using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Withdraws diagnostic work whose visit or stay closed before the work began, so that it neither waits on a worklist
/// nobody will work nor stays charged to the patient. Runs in the closing's unit of work.
/// <list type="bullet">
/// <item>A cancelled visit withdraws its tests whose sample is not drawn and its imaging and cardiology studies not done.</item>
/// <item>A cancelled admission withdraws the same of its stay, with the admission's reason.</item>
/// <item>A discharge withdraws the stay's tests whose sample is not drawn. A test already drawn carries on, so its result
/// reaches the record.</item>
/// </list>
/// What a clinician ordered is withdrawn by cancelling its order line, which cancels the work and its charge here as the
/// clinician's own cancellation does (<see cref="ServiceOrderCancelledHandler"/>), so the order, the department and the
/// bill say the same. A health checkup's tests are left to their booking, which is cancelled with its visit when its
/// package charge can be (<see cref="HealthCheckupService.CancelForVisitAsync"/>): the tests go with the charge, or stay.
/// </summary>
internal sealed class EncounterClosedHandlers(HospitalDbContext db, LabOrderService lab, ClinicalOrderService orders) :
    IDomainEventHandler<VisitCancelled>,
    IDomainEventHandler<AdmissionCancelled>,
    IDomainEventHandler<PatientDischarged>
{
    public async Task HandleAsync(VisitCancelled cancelled, CancellationToken cancellationToken)
    {
        var visit = await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(v => v.Id == cancelled.VisitId, cancellationToken);

        // An in-patient visit is cancelled only with its admission, whose own event withdraws the stay's work.
        if (visit is null || visit.Type == VisitType.InPatient)
        {
            return;
        }

        var reason = string.IsNullOrWhiteSpace(visit.CancelReason) ? "Visit cancelled" : $"Visit cancelled: {visit.CancelReason.Trim()}";
        await WithdrawAsync(cancelled.VisitId, null, reason, studies: true, cancellationToken);
    }

    public Task HandleAsync(AdmissionCancelled cancelled, CancellationToken cancellationToken) =>
        WithdrawAsync(cancelled.VisitId, cancelled.AdmissionId, $"Admission cancelled: {cancelled.Reason}", studies: true, cancellationToken);

    public Task HandleAsync(PatientDischarged discharged, CancellationToken cancellationToken) =>
        WithdrawAsync(discharged.VisitId, discharged.AdmissionId, "Patient discharged before the sample was collected", studies: false, cancellationToken);

    /// <summary>
    /// The work of the visit, or of the stay when <paramref name="admissionId"/> is given. Read under the encounter's lock,
    /// held to the closing's commit (<see cref="EncounterLocks"/>): an order being placed on it is committed first and is
    /// withdrawn here with the rest, and one placed after the closing finds the encounter closed and is refused.
    /// </summary>
    private async Task WithdrawAsync(Guid visitId, Guid? admissionId, string reason, bool studies, CancellationToken ct)
    {
        await EncounterLocks.LockAsync(db, [visitId, admissionId], ct);
        var lines = new List<Guid>(await lab.WithdrawUncollectedAsync(visitId, admissionId, reason, ct));
        if (studies)
        {
            // A study is done once the scan or the recording has begun. Studies come only from order lines.
            lines.AddRange(await db.Set<ImagingStudy>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(s => (s.VisitId == visitId || (admissionId != null && s.AdmissionId == admissionId)) && s.SourceOrderItemId != null
                            && (s.Status == ImagingStudyStatus.Ordered || s.Status == ImagingStudyStatus.Scheduled || s.Status == ImagingStudyStatus.Arrived))
                .Select(s => s.SourceOrderItemId!.Value)
                .ToListAsync(ct));
            lines.AddRange(await db.Set<CardiologyStudy>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(s => (s.VisitId == visitId || (admissionId != null && s.AdmissionId == admissionId)) && s.SourceOrderItemId != null
                            && (s.Status == CardiologyStudyStatus.Ordered || s.Status == CardiologyStudyStatus.Scheduled))
                .Select(s => s.SourceOrderItemId!.Value)
                .ToListAsync(ct));
        }

        await orders.WithdrawLinesAsync(lines, reason, ct);
    }
}
