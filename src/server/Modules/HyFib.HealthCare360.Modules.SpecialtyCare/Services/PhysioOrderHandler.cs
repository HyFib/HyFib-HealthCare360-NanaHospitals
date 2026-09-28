using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Turns EMR physiotherapy orders into physiotherapy referrals (PHY-01) inside the ordering transaction, so an order
/// can never exist without its work item. Idempotent on the order line id; cancelling the order cancels the referral
/// unless treatment has already been given.
/// </summary>
internal sealed class PhysioOrderHandler(HospitalDbContext db, TimeProvider time) :
    IDomainEventHandler<ServiceOrderPlaced>,
    IDomainEventHandler<ServiceOrderCancelled>
{
    public async Task HandleAsync(ServiceOrderPlaced domainEvent, CancellationToken cancellationToken)
    {
        if (domainEvent.Category != ServiceCategory.Physiotherapy
            || await db.Set<PhysioReferral>().IgnoreQueryFilters().AnyAsync(r => r.OrderItemId == domainEvent.OrderItemId, cancellationToken))
        {
            return;
        }

        db.Add(NewReferral(domainEvent));
    }

    /// <summary>The order quantity is the number of sessions the doctor prescribed.</summary>
    internal static PhysioReferral NewReferral(ServiceOrderPlaced domainEvent) => new()
    {
        FacilityId = domainEvent.FacilityId,
        PatientId = domainEvent.PatientId,
        VisitId = domainEvent.VisitId,
        AdmissionId = domainEvent.AdmissionId,
        OrderItemId = domainEvent.OrderItemId,
        OrderNumber = domainEvent.OrderNumber,
        ServiceItemId = domainEvent.ServiceItemId,
        ServiceName = domainEvent.ServiceName,
        SessionsOrdered = Math.Max(1, domainEvent.Quantity),
        ReferredByPractitionerId = domainEvent.OrderingPractitionerId,
        Priority = domainEvent.Priority,
        Reason = domainEvent.ClinicalNotes is { Length: > 0 } notes ? notes[..Math.Min(notes.Length, 1000)] : domainEvent.ServiceName,
        ReferredAt = domainEvent.OrderedAt,
        Status = PhysioReferralStatus.New,
    };

    public async Task HandleAsync(ServiceOrderCancelled domainEvent, CancellationToken cancellationToken)
    {
        var referral = await db.Set<PhysioReferral>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(r => r.OrderItemId == domainEvent.OrderItemId, cancellationToken);
        if (referral is null || !referral.IsOpen)
        {
            return;
        }

        var sessions = await db.Set<PhysioSession>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(s => s.ReferralId == referral.Id)
            .ToListAsync(cancellationToken);
        var reason = $"Order cancelled: {domainEvent.Reason}";
        if (referral.Cancel(reason, sessions.Any(s => s.Status == PhysioSessionStatus.Attended), time.GetUtcNow()).IsSuccess)
        {
            foreach (var session in sessions.Where(s => s.Status == PhysioSessionStatus.Scheduled))
            {
                session.Close(PhysioSessionStatus.Cancelled, reason);
            }
        }
    }
}
