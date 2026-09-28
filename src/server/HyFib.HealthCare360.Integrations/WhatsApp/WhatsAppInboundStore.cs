using HyFib.HealthCare360.Integrations.Domain;
using Npgsql;

namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>
/// Stores patient messages from the webhook. Meta redelivers webhooks until it gets a 200, so storage is idempotent
/// on the provider message id (checked first, and guarded by the unique index for concurrent deliveries).
/// </summary>
internal sealed class WhatsAppInboundStore(HospitalDbContext db, TimeProvider time)
{
    public async Task<int> SaveAsync(IReadOnlyList<WhatsAppInbound> messages, Guid? facilityId, CancellationToken cancellationToken)
    {
        var distinct = messages.DistinctBy(m => m.ProviderMessageId).ToList();
        if (distinct.Count == 0)
        {
            return 0;
        }

        var ids = distinct.Select(m => m.ProviderMessageId).ToList();
        var known = await db.Set<InboundWhatsAppMessage>().AsNoTracking()
            .Where(m => ids.Contains(m.ProviderMessageId))
            .Select(m => m.ProviderMessageId)
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        var added = 0;
        foreach (var message in distinct.Where(m => !known.Contains(m.ProviderMessageId)))
        {
            db.Add(new InboundWhatsAppMessage
            {
                ReceivedAt = now,
                SentAt = message.SentAt,
                FacilityId = facilityId,
                ProviderMessageId = IntegrationTrace.Truncate(message.ProviderMessageId, 128)!,
                FromPhone = Phone.Normalize("+" + message.From.TrimStart('+')) ?? IntegrationTrace.Truncate(message.From, 20)!,
                ProfileName = IntegrationTrace.Truncate(message.ProfileName, 128),
                MessageType = IntegrationTrace.Truncate(message.Type, 32)!,
                Text = message.Text,
                MediaId = IntegrationTrace.Truncate(message.MediaId, 128),
                ContextMessageId = IntegrationTrace.Truncate(message.ContextMessageId, 128),
                Keyword = WhatsAppWebhookParser.Classify(message.Text),
                PhoneNumberId = IntegrationTrace.Truncate(message.PhoneNumberId, 32),
            });
            added++;
        }

        if (added == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return added;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent redelivery stored the same message first; nothing is lost.
            return 0;
        }
    }
}
