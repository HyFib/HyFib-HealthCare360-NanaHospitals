using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Keeps the local admission projection in step with Inpatient (by id only), so OT, blood and IP dialysis charges
/// land on the running IP bill, brings the patient's waiting surgeries into the stay, and tidies up work that cannot
/// continue once the admission ends (discharge or cancellation).
/// </summary>
internal sealed class AdmissionEventHandlers(HospitalDbContext db, TimeProvider time) :
    IDomainEventHandler<PatientAdmitted>,
    IDomainEventHandler<PatientDischarged>,
    IDomainEventHandler<AdmissionCancelled>
{
    public async Task HandleAsync(PatientAdmitted domainEvent, CancellationToken cancellationToken)
    {
        var existing = await db.Set<AdmissionRecord>().IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == domainEvent.AdmissionId, cancellationToken);
        if (existing is not null)
        {
            return;
        }

        db.Add(new AdmissionRecord
        {
            Id = domainEvent.AdmissionId,
            FacilityId = domainEvent.FacilityId,
            PatientId = domainEvent.PatientId,
            VisitId = domainEvent.VisitId,
            AdmissionNumber = domainEvent.AdmissionNumber,
            AdmittedAt = domainEvent.AdmittedAt,
        });

        // A surgery booked at this hospital before the admission (at the OPD, or with no visit) and still waiting for its day
        // belongs to the stay from now on: its theatre and implant charges go to the running IP bill and count for the
        // discharge clearance. Its visit is kept, so that it can be given back if the stay ends first (CloseAsync).
        var waiting = await db.Set<SurgeryBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(b => b.PatientId == domainEvent.PatientId && b.FacilityId == domainEvent.FacilityId && b.AdmissionId == null
                        && (b.Status == SurgeryStatus.Requested || b.Status == SurgeryStatus.Scheduled || b.Status == SurgeryStatus.Postponed))
            .ToListAsync(cancellationToken);
        waiting.ForEach(b => b.AdmissionId = domainEvent.AdmissionId);
    }

    /// <summary>
    /// On discharge: close the projection; cancel surgeries still waiting that were booked during the stay (a new
    /// admission gets a new booking) and give back those booked before it; cancel open blood requests without issued units
    /// and free their reserved units for others; detach future dialysis sessions from the closed admission so they bill as
    /// out-patient sessions.
    /// </summary>
    public Task HandleAsync(PatientDischarged domainEvent, CancellationToken cancellationToken) =>
        CloseAsync(domainEvent.AdmissionId, domainEvent.DischargedAt, "Patient discharged before surgery", "Patient discharged", cancellationToken);

    /// <summary>A cancelled admission closes the same way: nothing more can be done or charged under it.</summary>
    public Task HandleAsync(AdmissionCancelled domainEvent, CancellationToken cancellationToken) =>
        CloseAsync(domainEvent.AdmissionId, domainEvent.CancelledAt, "Admission cancelled", "Admission cancelled", cancellationToken);

    private async Task CloseAsync(Guid admissionId, DateTimeOffset closedAt, string surgeryReason, string bloodReason, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var admission = await db.Set<AdmissionRecord>().IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == admissionId, cancellationToken);
        if (admission is not null)
        {
            // The projection only records whether the admission is still open.
            admission.DischargedAt ??= closedAt;
        }

        var bookings = await db.Set<SurgeryBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(b => b.AdmissionId == admissionId
                        && (b.Status == SurgeryStatus.Requested || b.Status == SurgeryStatus.Scheduled || b.Status == SurgeryStatus.Postponed))
            .ToListAsync(cancellationToken);
        foreach (var booking in bookings)
        {
            // A surgery booked before the stay joined it at admission: it goes back to waiting on its own, slot and all,
            // rather than being cancelled with a stay it was not booked for.
            if (admission is not null && booking.CreatedAt < admission.AdmittedAt)
            {
                booking.AdmissionId = null;
            }
            else
            {
                booking.Cancel(surgeryReason);
            }
        }

        var requests = await db.Set<BloodRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.AdmissionId == admissionId && r.UnitsIssued == 0
                        && r.Status != BloodRequestStatus.Issued && r.Status != BloodRequestStatus.Cancelled)
            .ToListAsync(cancellationToken);
        if (requests.Count > 0)
        {
            var requestIds = requests.Select(r => r.Id).ToList();
            var reserved = await db.Set<BloodComponent>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(c => c.ReservedForRequestId != null && requestIds.Contains(c.ReservedForRequestId.Value) && c.Status == BloodUnitStatus.Reserved)
                .ToListAsync(cancellationToken);
            reserved.ForEach(c => c.ReleaseReservation(now));
            requests.ForEach(r => r.Cancel(bloodReason, now));
        }

        var sessions = await db.Set<DialysisSession>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(s => s.AdmissionId == admissionId && s.Status == DialysisSessionStatus.Scheduled)
            .ToListAsync(cancellationToken);
        foreach (var session in sessions)
        {
            session.AdmissionId = null;
            session.VisitId = null;
        }
    }
}
