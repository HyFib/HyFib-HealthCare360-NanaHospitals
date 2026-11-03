using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Billing.Domain;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// What a gateway webhook is about: the event type and the gateway ids, nothing else. Razorpay's shape is
/// <c>{"event":"payment.captured","payload":{"payment":{"entity":{"id":"pay_…","order_id":"order_…"}}}}</c>; refunds,
/// payment links, orders, settlements and disputes sit under their own names. Ids that do not look like gateway ids are
/// dropped.
/// </summary>
public sealed partial record GatewayWebhookInfo(string? EventType, string? OrderId, string? PaymentId, string? RefundId, string? SettlementId, string? DisputeId = null)
{
    public static GatewayWebhookInfo Read(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new GatewayWebhookInfo(null, null, null, null, null);
            }

            var eventType = Text(root, "event") is { } name && EventPattern().IsMatch(name) ? name : null;
            var body = root.TryGetProperty("payload", out var p) ? p : default;
            return new GatewayWebhookInfo(
                eventType,
                Id(Entity(body, "payment"), "order_id") ?? Id(Entity(body, "order"), "id") ?? Id(Entity(body, "payment_link"), "id"),
                Id(Entity(body, "payment"), "id") ?? Id(Entity(body, "refund"), "payment_id") ?? Id(Entity(body, "dispute"), "payment_id"),
                Id(Entity(body, "refund"), "id"),
                Id(Entity(body, "settlement"), "id"),
                Id(Entity(body, "dispute"), "id"));
        }
        catch (JsonException)
        {
            return new GatewayWebhookInfo(null, null, null, null, null);
        }
    }

    /// <summary>
    /// What a Cashfree webhook names: <c>{"type":"PAYMENT_SUCCESS_WEBHOOK","event_time":…,"data":{…}}</c>, with the order
    /// and payment under <c>data.order</c> and <c>data.payment</c>, a refund under <c>data.refund</c>, a settlement under
    /// <c>data.settlement</c> (Cashfree's SDKs). Its dispute and payment-link events are not described there, so a
    /// <c>dispute_id</c> or <c>link_id</c> is taken from wherever it sits. Ids come as strings or numbers; anything that
    /// does not look like an id is dropped. Nothing else in the body is used: the gateway is asked.
    /// </summary>
    public static GatewayWebhookInfo ReadCashfree(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new GatewayWebhookInfo(null, null, null, null, null);
            }

            var type = Text(root, "type") is { } named && CashfreeTypePattern().IsMatch(named) ? named : null;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            var payment = Child(data, "payment");
            var refund = Child(data, "refund");
            var dispute = Find(data, "dispute_id", 0) is { } found && DisputeNumberPattern().IsMatch(found) ? found : null;
            return new GatewayWebhookInfo(
                type,
                CashfreeId(Find(data, "link_id", 0)) ?? CashfreeId(Text(Child(data, "order"), "order_id")) ?? CashfreeId(Text(payment, "order_id")),
                CashfreeId(Text(payment, "cf_payment_id")) ?? CashfreeId(Text(refund, "cf_payment_id")),
                CashfreeId(Text(refund, "refund_id")),
                CashfreeId(Text(Child(data, "settlement"), "cf_settlement_id")),
                dispute);
        }
        catch (JsonException)
        {
            return new GatewayWebhookInfo(null, null, null, null, null);
        }
    }

    /// <summary>The gateway's event id when it sends a usable one, else a SHA-256 of the body (an exact redelivery has the same).</summary>
    public static string EventIdOf(string? header, string payload) =>
        header is { Length: > 0 and <= 64 } id && HeaderPattern().IsMatch(id)
            ? id
            : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static JsonElement Entity(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var wrapper) && wrapper.ValueKind == JsonValueKind.Object
        && wrapper.TryGetProperty("entity", out var entity)
            ? entity
            : default;

    private static string? Id(JsonElement element, string name) => Text(element, name) is { } id && IdPattern().IsMatch(id) ? id : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number when value.TryGetInt64(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            }
            : null;

    private static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object ? child : default;

    /// <summary>The first <paramref name="name"/> found in <paramref name="element"/>, depth first, at most eight levels down.</summary>
    private static string? Find(JsonElement element, string name, int depth)
    {
        if (depth > 8)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (Text(element, name) is { } here)
            {
                return here;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (Find(property.Value, name, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray().Take(50))
            {
                if (Find(item, name, depth + 1) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? CashfreeId(string? id) => id is not null && CashfreeIdPattern().IsMatch(id) ? id : null;

    [GeneratedRegex("^[a-z]{2,8}_[A-Za-z0-9]{6,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z_]{2,32}(\\.[a-z_]{2,32}){0,2}$")]
    private static partial Regex EventPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex CashfreeIdPattern();

    [GeneratedRegex("^[A-Z_]{2,64}$")]
    private static partial Regex CashfreeTypePattern();

    [GeneratedRegex("^[0-9]{1,18}$")]
    private static partial Regex DisputeNumberPattern();
}

/// <summary>
/// Keeps verified gateway webhooks by event id (<see cref="GatewayWebhookEvent"/>), so an event the gateway delivers
/// again is not handled twice. It writes through its own database context: the record of a delivery must neither ride
/// on nor roll back with the receipt it leads to. Only signature-checked deliveries are recorded.
/// </summary>
public sealed class GatewayWebhookLog(IDbContextFactory<HospitalDbContext> contexts, TimeProvider time, ILogger<GatewayWebhookLog> logger)
{
    /// <summary>A delivery still being handled after this long stopped part way; the next one takes it over.</summary>
    public static readonly TimeSpan InFlight = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Records a delivery. Returns null when the event was already handled, or is being handled right now by another
    /// delivery: the caller acknowledges it without acting. An event whose earlier handling failed, or stopped part way
    /// (still being handled after <see cref="InFlight"/>), is handled again by exactly one delivery: the row is taken
    /// over with a conditional update.
    /// </summary>
    public async Task<Guid?> BeginAsync(string provider, string eventId, GatewayWebhookInfo info, Guid? facilityId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var existing = await db.Set<GatewayWebhookEvent>().AsNoTracking()
            .Where(e => e.Provider == provider && e.EventId == eventId)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } id)
        {
            var stale = now - InFlight;
            var taken = await db.Set<GatewayWebhookEvent>()
                .Where(e => e.Id == id)
                .Where(e => e.Outcome == WebhookEventOutcome.Failed
                            || (e.Outcome == WebhookEventOutcome.Received && e.StartedAt < stale))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(e => e.Outcome, WebhookEventOutcome.Received)
                    .SetProperty(e => e.StartedAt, now)
                    .SetProperty(e => e.Deliveries, e => e.Deliveries + 1), ct);
            if (taken == 1)
            {
                return id;
            }

            await db.Set<GatewayWebhookEvent>().Where(e => e.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.Deliveries, e => e.Deliveries + 1), ct);
            return null;
        }

        var row = new GatewayWebhookEvent
        {
            Provider = provider,
            EventId = eventId,
            EventType = info.EventType,
            OrderId = info.OrderId,
            PaymentId = info.PaymentId,
            RefundId = info.RefundId,
            SettlementId = info.SettlementId,
            DisputeId = info.DisputeId,
            FacilityId = facilityId,
            ReceivedAt = now,
            StartedAt = now,
            Outcome = WebhookEventOutcome.Received,
            Deliveries = 1,
        };
        db.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return row.Id;
        }
        catch (DbUpdateException ex)
        {
            // The same event arrived twice at once: the other delivery handles it.
            logger.LogInformation(ex, "Gateway webhook event {EventId} is already being handled", eventId);
            return null;
        }
    }

    /// <summary>
    /// Records how the handling went. An event already processed stays processed: a delivery that took over one that
    /// only seemed to have stopped must not mark it failed after the other one did the work.
    /// </summary>
    public async Task CompleteAsync(Guid id, WebhookEventOutcome outcome, string? note, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var handledAt = time.GetUtcNow();
        var clipped = note is { Length: > 300 } ? note[..300] : note;
        await db.Set<GatewayWebhookEvent>()
            .Where(e => e.Id == id)
            .Where(e => outcome == WebhookEventOutcome.Processed || e.Outcome != WebhookEventOutcome.Processed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.Outcome, outcome)
                .SetProperty(e => e.HandledAt, handledAt)
                .SetProperty(e => e.Note, clipped), ct);
    }
}
