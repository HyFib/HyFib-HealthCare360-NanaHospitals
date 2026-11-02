using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// A health check-up package is shown and booked at its service's price, and only while the service may be charged.
/// Whichever way the service's price terms were set (the package's form, the services master, or an approval of either),
/// the package follows in the same transaction, so the price the desk quotes is the one billing charges at check-in.
/// </summary>
internal sealed class CheckupPackageFollowsServicePrice(HospitalDbContext db) : IDomainEventHandler<ServicePriceChanged>
{
    public async Task HandleAsync(ServicePriceChanged domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var packages = await db.Set<HealthPackage>().Where(p => p.ServiceItemId == domainEvent.ServiceItemId).ToListAsync(cancellationToken);

        // A package added in this unit of work is not in the database yet.
        packages.AddRange(db.ChangeTracker.Entries<HealthPackage>()
            .Where(e => e.State == EntityState.Added && e.Entity.ServiceItemId == domainEvent.ServiceItemId)
            .Select(e => e.Entity));
        foreach (var package in packages)
        {
            package.Price = domainEvent.BasePrice;
            package.IsActive = domainEvent.IsActive;
        }
    }
}
