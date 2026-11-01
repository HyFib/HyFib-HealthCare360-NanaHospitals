using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Handlers;

/// <summary>
/// A visit cancelled at the desk cancels the health check-ups checked in on it, in the cancellation's unit of work, as the
/// check-up's own cancellation does (<see cref="HealthCheckupService.CancelForVisitAsync"/>). A check-up's tests are charged
/// once, as its package, so they are withdrawn only with the package charge: by the booking, never on their own.
/// </summary>
internal sealed class HealthCheckupVisitCancelledHandler(HospitalDbContext db, HealthCheckupService checkups) : IDomainEventHandler<VisitCancelled>
{
    public async Task HandleAsync(VisitCancelled cancelled, CancellationToken cancellationToken)
    {
        var visit = await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(v => v.Id == cancelled.VisitId, cancellationToken);

        // An in-patient visit is cancelled only with its admission; no check-up is checked in on one.
        if (visit is null || visit.Type == VisitType.InPatient)
        {
            return;
        }

        var reason = string.IsNullOrWhiteSpace(visit.CancelReason) ? "Visit cancelled" : $"Visit cancelled: {visit.CancelReason.Trim()}";
        await checkups.CancelForVisitAsync(visit.Id, visit.VisitNumber, reason, cancellationToken);
    }
}
