using HyFib.HealthCare360.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Engagement.Services.Handlers;

/// <summary>
/// A STOP or START on WhatsApp (raised by Integrations once per new reply) updates the marketing consent of every patient
/// registered with the number, in the webhook's transaction. The platform keeps the number's WhatsApp opt-out itself.
/// </summary>
internal sealed class WhatsAppOptOutChangedHandler(CommunicationConsentService consents, ILogger<WhatsAppOptOutChangedHandler> logger)
    : IDomainEventHandler<WhatsAppOptOutChanged>
{
    public async Task HandleAsync(WhatsAppOptOutChanged reply, CancellationToken cancellationToken)
    {
        var changed = await consents.ApplyWhatsAppReplyAsync(reply.Phone, reply.OptedOut, reply.RepliedAt, cancellationToken);
        if (changed > 0)
        {
            logger.LogInformation("A WhatsApp {Reply} reply changed the marketing consent of {Count} patient(s)", reply.OptedOut ? "STOP" : "START", changed);
        }
    }
}
