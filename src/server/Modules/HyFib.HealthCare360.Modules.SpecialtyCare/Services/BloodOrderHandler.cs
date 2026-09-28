using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Turns EMR blood orders (BB-WB, BB-PRBC, BB-FFP, BB-PLT, BB-CRYO) into blood bank requests in the ordering
/// transaction; the order quantity is the number of units requested.
/// Cancelling the order cancels the request and frees its reserved units, unless blood was already issued.
/// </summary>
internal sealed class BloodOrderHandler(HospitalDbContext db, INumberSeries numbers, TimeProvider time) :
    IDomainEventHandler<ServiceOrderPlaced>,
    IDomainEventHandler<ServiceOrderCancelled>
{
    public async Task HandleAsync(ServiceOrderPlaced domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.Category != ServiceCategory.BloodBank
            || BloodBankServiceCodes.ComponentFor(domainEvent.ServiceCode) is not { } component
            || await db.Set<BloodRequest>().IgnoreQueryFilters().AnyAsync(r => r.OrderItemId == domainEvent.OrderItemId, cancellationToken))
        {
            return;
        }

        var request = NewRequest(domainEvent, component);
        request.RequestNumber = await numbers.NextAsync(TransfusionService.RequestSeries, domainEvent.FacilityId, TransfusionService.RequestFormat,
            NumberResetPolicy.Yearly, cancellationToken);
        db.Add(request);
    }

    /// <summary>Stat orders become emergency requests (they alert issuers and allow authorised emergency release).</summary>
    internal static BloodRequest NewRequest(ServiceOrderPlaced domainEvent, BloodComponentType component) => new()
    {
        FacilityId = domainEvent.FacilityId,
        PatientId = domainEvent.PatientId,
        AdmissionId = domainEvent.AdmissionId,
        VisitId = domainEvent.VisitId,
        OrderItemId = domainEvent.OrderItemId,
        ComponentType = component,
        UnitsRequested = Math.Max(1, domainEvent.Quantity),
        Urgency = domainEvent.Priority switch
        {
            OrderPriority.Stat => BloodRequestUrgency.Emergency,
            OrderPriority.Urgent => BloodRequestUrgency.Urgent,
            _ => BloodRequestUrgency.Routine,
        },
        Indication = domainEvent.ClinicalNotes is { Length: > 0 } notes ? notes[..Math.Min(notes.Length, 1000)] : domainEvent.ServiceName,
        RequestedByPractitionerId = domainEvent.OrderingPractitionerId,
        Status = BloodRequestStatus.Pending,
    };

    public async Task HandleAsync(ServiceOrderCancelled domainEvent, CancellationToken cancellationToken)
    {
        var request = await db.Set<BloodRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(r => r.OrderItemId == domainEvent.OrderItemId, cancellationToken);
        if (request is null || !request.IsOpen || request.UnitsIssued > 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        request.Cancel($"Order cancelled: {domainEvent.Reason}", now);
        var reserved = await db.Set<BloodComponent>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(c => c.ReservedForRequestId == request.Id && c.Status == BloodUnitStatus.Reserved)
            .ToListAsync(cancellationToken);
        reserved.ForEach(c => c.ReleaseReservation(now));
    }
}
