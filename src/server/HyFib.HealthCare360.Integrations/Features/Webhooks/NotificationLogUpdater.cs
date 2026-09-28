using HyFib.HealthCare360.Integrations.Sms;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Integrations.Features.Webhooks;

/// <summary>
/// Applies delivery receipts to the platform's notification log, matched by the provider message id stored when the
/// message was sent: a delivery marks it <c>Delivered</c> (clearing an earlier transport error), a failure marks it
/// <c>Failed</c> with the provider's reason.
/// </summary>
internal sealed class NotificationLogUpdater(HospitalDbContext db, TimeProvider time)
{
    public async Task<int> ApplyAsync(IEnumerable<DeliveryReceipt> receipts, CancellationToken cancellationToken)
    {
        var updated = 0;
        foreach (var receipt in receipts.Where(r => r.Outcome != DeliveryOutcome.Pending && r.ProviderMessageId.Length <= 128))
        {
            var id = receipt.ProviderMessageId;
            if (receipt.Outcome == DeliveryOutcome.Delivered)
            {
                var now = time.GetUtcNow();
                updated += await db.Set<NotificationLog>()
                    .Where(l => l.ProviderMessageId == id && l.Status != NotificationStatus.Skipped)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(l => l.Status, NotificationStatus.Delivered)
                        .SetProperty(l => l.Error, (string?)null)
                        .SetProperty(l => l.SentAt, l => l.SentAt ?? now)
                        .SetProperty(l => l.DeliveredAt, l => l.DeliveredAt ?? now), cancellationToken);
            }
            else
            {
                var error = IntegrationTrace.Truncate("Delivery failed: " + (receipt.Detail ?? "reported by the provider"), 1000);
                updated += await db.Set<NotificationLog>()
                    .Where(l => l.ProviderMessageId == id)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(l => l.Status, NotificationStatus.Failed)
                        .SetProperty(l => l.Error, error), cancellationToken);
            }
        }

        return updated;
    }
}
