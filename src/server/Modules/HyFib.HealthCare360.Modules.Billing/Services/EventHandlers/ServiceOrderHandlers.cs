using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;

/// <summary>Which clinical orders Billing prices itself when they are placed (MB-01).</summary>
internal static class OrderPricing
{
    /// <summary>
    /// Categories no other module charges for. Laboratory, radiology and cardiology are charged by Diagnostics; surgery,
    /// physiotherapy, dialysis and blood by SpecialtyCare; diet by SupportServices; consultations by the visit (OP) or the
    /// consultant-visit record (IP). An allow-list, so a module that starts charging a category can never double-charge.
    /// </summary>
    public static IReadOnlySet<ServiceCategory> BilledOnOrder { get; } = new HashSet<ServiceCategory> { ServiceCategory.Procedure };

    /// <summary>Health-checkup components are billed once, under the package's own service item.</summary>
    public static bool IsBilledOnOrder(ServiceOrderPlaced order) => !order.IsPackageComponent && BilledOnOrder.Contains(order.Category);
}

/// <summary>
/// Prices a procedure when the clinician orders it (source <c>billing.order</c>, reference = order item id), in the
/// order's transaction. A pricing problem is logged, never allowed to block the clinical order.
/// </summary>
internal sealed class ServiceOrderPlacedChargeHandler(ChargeService charges, ILogger<ServiceOrderPlacedChargeHandler> logger)
    : IDomainEventHandler<ServiceOrderPlaced>
{
    public async Task HandleAsync(ServiceOrderPlaced order, CancellationToken cancellationToken)
    {
        if (!OrderPricing.IsBilledOnOrder(order))
        {
            return;
        }

        var result = await charges.StageAsync(
            new ChargeRequest
            {
                PatientId = order.PatientId,
                VisitId = order.VisitId,
                AdmissionId = order.AdmissionId,
                ServiceItemId = order.ServiceItemId,
                Quantity = Math.Max(order.Quantity, 1),
                Description = order.ServiceName,
                SourceModule = ChargeService.OrderSource,
                SourceReference = order.OrderItemId.ToString("N"),
                PractitionerId = order.OrderingPractitionerId,
                ServiceDate = order.OrderedAt,
            },
            isFreeFollowUp: false,
            cancellationToken,
            facilityHint: order.FacilityId);
        if (result.IsFailure)
        {
            logger.LogWarning("Order {OrderNumber} ({ServiceCode}) was not charged: {Error}", order.OrderNumber, order.ServiceCode, result.Error!.Message);
        }
    }
}

/// <summary>
/// Cancels the charge of a cancelled order line. If it is already on a finalized bill the clinical cancellation still
/// stands and the billing desk is asked to credit the bill.
/// </summary>
internal sealed class ServiceOrderCancelledChargeHandler(
    ChargeService charges,
    INotificationService notifications,
    ILogger<ServiceOrderCancelledChargeHandler> logger) : IDomainEventHandler<ServiceOrderCancelled>
{
    public async Task HandleAsync(ServiceOrderCancelled cancelled, CancellationToken cancellationToken)
    {
        var charge = await charges.FindBySourceAsync(ChargeService.OrderSource, cancelled.OrderItemId.ToString("N"), cancellationToken);
        if (charge is null)
        {
            return;
        }

        var reason = $"Order cancelled: {cancelled.Reason}".Trim();
        var result = await charges.CancelChargeAsync(charge, reason.Length > 300 ? reason[..300] : reason, cancellationToken);
        if (result.IsSuccess)
        {
            return;
        }

        logger.LogWarning("Charge {ChargeId} of cancelled order line {OrderItemId} stays billed: {Error}", charge.Id, cancelled.OrderItemId, result.Error!.Message);
        await notifications.NotifyPermissionHoldersAsync(
            BillingPermissions.RefundsApprove,
            charge.FacilityId,
            "Cancelled order already billed",
            $"{charge.Description} was cancelled after it was billed. Issue a credit note on the bill.",
            charge.BillId is { } billId ? $"/billing/bills/{billId}" : null,
            "billing",
            "warning",
            cancellationToken);
    }
}
