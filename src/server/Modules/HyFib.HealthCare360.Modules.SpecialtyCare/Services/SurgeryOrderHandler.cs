using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Turns an EMR surgery order into a surgery request on the OT list (OT-01), with the ordering doctor as surgeon.
/// The OT coordinator then confirms the team and anaesthesia and gives it a theatre slot. Cancelling the order
/// cancels the booking as long as surgery has not started. Ordered while the patient is in a bed, from whatever visit,
/// the surgery belongs to the stay.
/// </summary>
internal sealed class SurgeryOrderHandler(HospitalDbContext db, INumberSeries numbers) :
    IDomainEventHandler<ServiceOrderPlaced>,
    IDomainEventHandler<ServiceOrderCancelled>
{
    public async Task HandleAsync(ServiceOrderPlaced domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.Category != ServiceCategory.Surgery
            || await db.Set<SurgeryBooking>().IgnoreQueryFilters().AnyAsync(b => b.OrderItemId == domainEvent.OrderItemId, cancellationToken))
        {
            return;
        }

        var (admissionId, visitId) = await SpecialtyLookups.SurgeryEncounterAsync(
            db, domainEvent.PatientId, domainEvent.AdmissionId, domainEvent.VisitId, cancellationToken);
        db.Add(new SurgeryBooking
        {
            FacilityId = domainEvent.FacilityId,
            BookingNumber = await numbers.NextAsync(SpecialtyCareNumberSeries.SurgeryBooking, domainEvent.FacilityId, cancellationToken),
            PatientId = domainEvent.PatientId,
            AdmissionId = admissionId,
            VisitId = visitId,
            OrderItemId = domainEvent.OrderItemId,
            ProcedureServiceItemId = domainEvent.ServiceItemId,
            ProcedureCode = domainEvent.ServiceCode,
            ProcedureName = domainEvent.ServiceName,
            Diagnosis = domainEvent.ClinicalNotes is { Length: > 0 } notes ? notes[..Math.Min(notes.Length, 500)] : null,
            SurgeonId = domainEvent.OrderingPractitionerId,
            Priority = domainEvent.Priority switch
            {
                OrderPriority.Stat => SurgeryPriority.Emergency,
                OrderPriority.Urgent => SurgeryPriority.Urgent,
                _ => SurgeryPriority.Elective,
            },
            Status = SurgeryStatus.Requested,
        });
    }

    public async Task HandleAsync(ServiceOrderCancelled domainEvent, CancellationToken cancellationToken)
    {
        var booking = await db.Set<SurgeryBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(b => b.OrderItemId == domainEvent.OrderItemId, cancellationToken);
        booking?.Cancel($"Order cancelled: {domainEvent.Reason}");
    }
}
