using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Creates diagnostic work from a clinician's order (ServiceOrderPlaced raised by Clinical): a lab accession item, an
/// imaging study or a cardiology study depending on the service category. Runs inside the ordering transaction, so the
/// order and its work items are committed together or not at all.
/// </summary>
internal sealed class ServiceOrderPlacedHandler(LabOrderService lab, ImagingService imaging, CardiologyService cardiology)
    : IDomainEventHandler<ServiceOrderPlaced>
{
    public Task HandleAsync(ServiceOrderPlaced domainEvent, CancellationToken cancellationToken) => domainEvent.Category switch
    {
        ServiceCategory.Laboratory => lab.CreateFromClinicalOrderAsync(domainEvent, cancellationToken),
        ServiceCategory.Radiology => imaging.CreateFromClinicalOrderAsync(domainEvent, cancellationToken),
        ServiceCategory.Cardiology => cardiology.CreateFromClinicalOrderAsync(domainEvent, cancellationToken),
        _ => Task.CompletedTask,
    };
}

/// <summary>
/// Cancels the matching lab test, imaging study or cardiology study when the clinician cancels the ordered item.
/// Work that is already verified is not undone; the lab/department is alerted to review it instead.
/// </summary>
internal sealed class ServiceOrderCancelledHandler(LabOrderService lab, ImagingService imaging, CardiologyService cardiology)
    : IDomainEventHandler<ServiceOrderCancelled>
{
    public async Task HandleAsync(ServiceOrderCancelled domainEvent, CancellationToken cancellationToken)
    {
        if (await lab.HandleClinicalCancelAsync(domainEvent, cancellationToken))
        {
            return;
        }

        if (await imaging.HandleClinicalCancelAsync(domainEvent, cancellationToken))
        {
            return;
        }

        await cardiology.HandleClinicalCancelAsync(domainEvent, cancellationToken);
    }
}
