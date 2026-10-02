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
/// payment links, orders and settlements sit under their own names. Ids that do not look like gateway ids are dropped.
/// </summary>
public sealed partial record GatewayWebhookInfo(string? EventType, string? OrderId, string? PaymentId, string? RefundId, string? SettlementId)
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
                Id(Entity(body, "payment"), "id") ?? Id(Entity(body, "refund"), "payment_id"),
                Id(Entity(body, "refund"), "id"),
                Id(Entity(body, "settlement"), "id"));
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
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("^[a-z]{2,8}_[A-Za-z0-9]{6,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[a-z_]{2,32}(\\.[a-z_]{2,32}){0,2}$")]
    private static partial Regex EventPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex HeaderPattern();
}

/// <summary>
/// Keeps verified gateway webhooks by event id (<see cref="GatewayWebhookEvent"/>), so an event the gateway delivers
/// again is not handled twice. It writes through its own database context: the record of a delivery must neither ride
/// on nor roll back with the receipt it leads to. Only signature-checked deliveries are recorded.
/// </summary>
public sealed class GatewayWebhookLog(IDbContextFactory<HospitalDbContext> contexts, TimeProvider time, ILogger<GatewayWebhookLog> logger)
{
    /// <summary>
    /// Records a delivery. Returns null when the event was already handled (or is being handled right now by a
    /// concurrent delivery): the caller acknowledges it without acting. An event whose earlier handling failed or
    /// stopped part way is handled again.
    /// </summary>
    public async Task<Guid?> BeginAsync(string provider, string eventId, GatewayWebhookInfo info, Guid? facilityId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var existing = await db.Set<GatewayWebhookEvent>().FirstOrDefaultAsync(e => e.Provider == provider && e.EventId == eventId, ct);
        if (existing is not null)
        {
            existing.Deliveries++;
            var handled = existing.Outcome is WebhookEventOutcome.Processed or WebhookEventOutcome.Ignored;
            if (!handled)
            {
                existing.Outcome = WebhookEventOutcome.Received;
            }

            await db.SaveChangesAsync(ct);
            return handled ? null : existing.Id;
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
            FacilityId = facilityId,
            ReceivedAt = now,
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

    public async Task CompleteAsync(Guid id, WebhookEventOutcome outcome, string? note, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<GatewayWebhookEvent>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row is null)
        {
            return;
        }

        row.Outcome = outcome;
        row.HandledAt = time.GetUtcNow();
        row.Note = note is { Length: > 300 } ? note[..300] : note;
        await db.SaveChangesAsync(ct);
    }
}
