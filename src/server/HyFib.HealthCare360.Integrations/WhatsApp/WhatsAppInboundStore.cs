using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Persistence;
using HyFib.HealthCare360.Platform.Messaging;
using Npgsql;

namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>
/// Stores patient messages from the webhook. Meta redelivers webhooks until it gets a 200, so storage is idempotent
/// on the provider message id (checked first, and guarded by the unique index for concurrent deliveries). A STOP or
/// START is acted on only when its message is stored for the first time (see <see cref="WhatsAppReplies"/>), in the
/// same transaction, and the sender is sent a confirmation while WhatsApp still allows a free-form reply.
/// </summary>
internal sealed class WhatsAppInboundStore(HospitalDbContext db, IOutbox outbox, TimeProvider time)
{
    public async Task<int> SaveAsync(IReadOnlyList<WhatsAppInbound> messages, Guid? facilityId, CancellationToken cancellationToken)
    {
        var ids = messages.Select(m => m.ProviderMessageId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        var known = await db.Set<InboundWhatsAppMessage>().AsNoTracking()
            .Where(m => ids.Contains(m.ProviderMessageId))
            .Select(m => m.ProviderMessageId)
            .ToListAsync(cancellationToken);

        var now = time.GetUtcNow();
        var received = NewMessages(messages, known, facilityId, now);
        if (received.Count == 0)
        {
            return 0;
        }

        var replies = received.Where(m => m.Keyword != WhatsAppKeyword.None).ToList();
        if (replies.Count > 0)
        {
            var phones = replies.Select(m => m.FromPhone).Distinct().ToList();
            var latest = await WhatsAppReplies.Latest(db.Set<InboundWhatsAppMessage>().AsNoTracking(), phones)
                .ToDictionaryAsync(l => l.Phone, l => l.RepliedAt, cancellationToken);
            WhatsAppReplies.Decide(replies, latest);
            foreach (var reply in replies.Where(r => r.KeywordAction is WhatsAppKeywordAction.OptedOut or WhatsAppKeywordAction.OptedIn))
            {
                QueueConfirmation(reply, now);
            }
        }

        db.AddRange(received);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return received.Count;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation,
                                               ConstraintName: InboundWhatsAppMessageConfiguration.ProviderMessageIdIndex,
                                           })
        {
            // A concurrent redelivery stored the same messages first and acted on their keywords; nothing is lost. Any
            // other conflict fails the request, so that Meta delivers the webhook again.
            return 0;
        }
    }

    /// <summary>The messages not stored yet, each once, with their keywords classified but not acted on.</summary>
    internal static List<InboundWhatsAppMessage> NewMessages(
        IReadOnlyList<WhatsAppInbound> messages, IReadOnlyCollection<string> known, Guid? facilityId, DateTimeOffset now) =>
        messages.DistinctBy(m => m.ProviderMessageId)
            .Where(m => !known.Contains(m.ProviderMessageId))
            .Select(message => new InboundWhatsAppMessage
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
                KeywordAction = WhatsAppKeywordAction.None,
                PhoneNumberId = IntegrationTrace.Truncate(message.PhoneNumberId, 32),
            })
            .ToList();

    private void QueueConfirmation(InboundWhatsAppMessage reply, DateTimeOffset now)
    {
        // WhatsApp accepts a free-form reply only within 24 hours of the patient's message; a late webhook gets none.
        var window = reply.RepliedAt + WhatsAppReplyConfirmationHandler.ReplyWindow - now;
        if (window <= TimeSpan.Zero)
        {
            return;
        }

        outbox.Enqueue(
            WhatsAppReplyConfirmationHandler.MessageType,
            new WhatsAppReplyConfirmation(reply.Id, reply.FromPhone, reply.KeywordAction == WhatsAppKeywordAction.OptedOut, reply.FacilityId),
            reply.FacilityId,
            expiresIn: window);
    }
}
