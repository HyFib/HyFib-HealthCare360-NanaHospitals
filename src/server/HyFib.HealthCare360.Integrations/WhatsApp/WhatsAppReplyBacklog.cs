using HyFib.HealthCare360.Integrations.Domain;

namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>
/// Acts, once, on STOP and START replies stored before HC360 acted on them (their action is still empty), by the same
/// rule as new replies: only each number's most recent reply takes effect. No confirmation is sent, because WhatsApp
/// allows a free-form reply only within 24 hours of the patient's message. It runs at start-up after the other
/// seeders; a failure is logged and retried at the next start rather than stopping HC360.
/// </summary>
internal sealed class WhatsAppReplyBacklog(ILogger<WhatsAppReplyBacklog> logger) : ISeeder
{
    public int Order => 500;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var pending = await db.Set<InboundWhatsAppMessage>()
                .Where(m => m.KeywordAction == null && m.Keyword != WhatsAppKeyword.None)
                .ToListAsync(cancellationToken);
            if (pending.Count == 0)
            {
                return;
            }

            var phones = pending.Select(m => m.FromPhone).Distinct().ToList();
            var decided = db.Set<InboundWhatsAppMessage>().AsNoTracking().Where(m => m.KeywordAction != null);
            var latest = await WhatsAppReplies.Latest(decided, phones).ToDictionaryAsync(l => l.Phone, l => l.RepliedAt, cancellationToken);
            WhatsAppReplies.Decide(pending, latest);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Acted on {Count} WhatsApp STOP/START replies received before HC360 handled them",
                pending.Count(m => m.KeywordAction is WhatsAppKeywordAction.OptedOut or WhatsAppKeywordAction.OptedIn));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not act on earlier WhatsApp STOP/START replies; HC360 will try again at the next start");
        }
    }
}
