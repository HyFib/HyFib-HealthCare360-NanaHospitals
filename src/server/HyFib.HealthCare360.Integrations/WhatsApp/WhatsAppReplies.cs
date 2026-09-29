using HyFib.HealthCare360.Integrations.Domain;

namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>When the most recent STOP or START stored from a number was sent.</summary>
internal sealed record LatestReply(string Phone, DateTimeOffset RepliedAt);

/// <summary>
/// Decides which STOP and START replies take effect. Meta can deliver webhooks late and out of order, so for each
/// number only its most recent reply counts: a reply older than one already stored from the number, or than another
/// in the same batch, is kept for the inbox but marked superseded and changes nothing.
/// </summary>
internal static class WhatsAppReplies
{
    /// <summary>The most recent STOP or START stored from each of <paramref name="phones"/>.</summary>
    public static IQueryable<LatestReply> Latest(IQueryable<InboundWhatsAppMessage> messages, IReadOnlyCollection<string> phones) =>
        messages
            .Where(m => m.Keyword != WhatsAppKeyword.None && phones.Contains(m.FromPhone))
            .GroupBy(m => m.FromPhone)
            .Select(g => new LatestReply(g.Key, g.Max(m => m.SentAt ?? m.ReceivedAt)));

    /// <summary>Acts on each number's newest STOP or START unless a later one is already stored, and supersedes the rest.</summary>
    public static void Decide(IEnumerable<InboundWhatsAppMessage> messages, IReadOnlyDictionary<string, DateTimeOffset> latestStored)
    {
        foreach (var number in messages.Where(m => m.Keyword != WhatsAppKeyword.None).GroupBy(m => m.FromPhone, StringComparer.Ordinal))
        {
            // OrderBy is stable: of two replies sent in the same second, the later one in the batch wins.
            var newest = number.OrderBy(m => m.RepliedAt).Last();
            var current = !latestStored.TryGetValue(number.Key, out var stored) || newest.RepliedAt >= stored;
            foreach (var message in number)
            {
                if (current && ReferenceEquals(message, newest))
                {
                    message.ApplyKeyword();
                }
                else
                {
                    message.SupersedeKeyword();
                }
            }
        }
    }
}
