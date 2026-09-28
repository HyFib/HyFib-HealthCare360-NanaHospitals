using System.Globalization;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Sms;

namespace HyFib.HealthCare360.Integrations.WhatsApp;

internal sealed record WhatsAppInbound(
    string ProviderMessageId,
    string From,
    string? ProfileName,
    string Type,
    string? Text,
    string? MediaId,
    string? ContextMessageId,
    DateTimeOffset? SentAt,
    string? PhoneNumberId);

internal sealed record WhatsAppWebhookBatch(IReadOnlyList<DeliveryReceipt> Statuses, IReadOnlyList<WhatsAppInbound> Messages);

/// <summary>
/// Parses Cloud API webhook POSTs (<c>entry[].changes[].value</c>): delivery statuses for messages HC360 sent and
/// messages patients sent to the hospital number. Unknown message types are kept with their type so nothing is lost.
/// </summary>
internal static class WhatsAppWebhookParser
{
    public static WhatsAppWebhookBatch Parse(string json)
    {
        var statuses = new List<DeliveryReceipt>();
        var messages = new List<WhatsAppInbound>();
        if (!IntegrationHttp.TryParseJson(json, out var document))
        {
            return new WhatsAppWebhookBatch(statuses, messages);
        }

        using (document)
        {
            foreach (var value in Values(document.RootElement))
            {
                var phoneNumberId = IntegrationHttp.ReadPath(value, "metadata.phone_number_id");
                var names = Contacts(value);
                if (value.TryGetProperty("statuses", out var statusArray) && statusArray.ValueKind == JsonValueKind.Array)
                {
                    statuses.AddRange(statusArray.EnumerateArray().Select(Status).OfType<DeliveryReceipt>());
                }

                if (value.TryGetProperty("messages", out var messageArray) && messageArray.ValueKind == JsonValueKind.Array)
                {
                    messages.AddRange(messageArray.EnumerateArray().Select(m => Inbound(m, names, phoneNumberId)).OfType<WhatsAppInbound>());
                }
            }
        }

        return new WhatsAppWebhookBatch(statuses, messages);
    }

    /// <summary>Exact STOP/START-style keywords only, so ordinary replies are never read as an opt-out.</summary>
    public static WhatsAppKeyword Classify(string? text) => text?.Trim().TrimEnd('.', '!').ToUpperInvariant() switch
    {
        "STOP" or "UNSUBSCRIBE" or "OPT OUT" or "OPTOUT" or "STOP ALL" => WhatsAppKeyword.OptOut,
        "START" or "SUBSCRIBE" or "OPT IN" or "OPTIN" or "UNSTOP" => WhatsAppKeyword.OptIn,
        _ => WhatsAppKeyword.None,
    };

    private static IEnumerable<JsonElement> Values(JsonElement root)
    {
        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray())
            {
                if (change.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                {
                    yield return value;
                }
            }
        }
    }

    private static Dictionary<string, string> Contacts(JsonElement value)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
        {
            foreach (var contact in contacts.EnumerateArray())
            {
                if (IntegrationHttp.Property(contact, "wa_id") is { } waId && IntegrationHttp.ReadPath(contact, "profile.name") is { } name)
                {
                    names[waId] = name;
                }
            }
        }

        return names;
    }

    private static DeliveryReceipt? Status(JsonElement status)
    {
        var id = IntegrationHttp.Property(status, "id");
        var state = IntegrationHttp.Property(status, "status")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        var outcome = state switch
        {
            "delivered" or "read" => DeliveryOutcome.Delivered,
            "failed" => DeliveryOutcome.Failed,
            _ => DeliveryOutcome.Pending,
        };
        var error = IntegrationHttp.ReadPath(status, "errors.0.title") ?? IntegrationHttp.ReadPath(status, "errors.0.message");
        var code = IntegrationHttp.ReadPath(status, "errors.0.code");
        var detail = outcome == DeliveryOutcome.Failed ? $"WhatsApp {(code is null ? string.Empty : code + " ")}{error ?? "delivery failed"}".Trim() : state;
        return new DeliveryReceipt(id, outcome, detail);
    }

    private static WhatsAppInbound? Inbound(JsonElement message, IReadOnlyDictionary<string, string> names, string? phoneNumberId)
    {
        var id = IntegrationHttp.Property(message, "id");
        var from = IntegrationHttp.Property(message, "from");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(from))
        {
            return null;
        }

        var type = IntegrationHttp.Property(message, "type") ?? "unknown";
        var (text, mediaId) = type switch
        {
            "text" => (IntegrationHttp.ReadPath(message, "text.body"), null),
            "button" => (IntegrationHttp.ReadPath(message, "button.text"), null),
            "interactive" => (IntegrationHttp.ReadPath(message, "interactive.button_reply.title") ?? IntegrationHttp.ReadPath(message, "interactive.list_reply.title"), null),
            "image" or "document" or "audio" or "video" or "sticker" => (IntegrationHttp.ReadPath(message, $"{type}.caption"), IntegrationHttp.ReadPath(message, $"{type}.id")),
            "reaction" => (IntegrationHttp.ReadPath(message, "reaction.emoji"), null),
            "location" => (FormatLocation(message), null),
            _ => ((string?)null, (string?)null),
        };

        DateTimeOffset? sentAt = long.TryParse(IntegrationHttp.Property(message, "timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
        return new WhatsAppInbound(id, from, names.GetValueOrDefault(from), type, IntegrationTrace.Truncate(text, 4096), mediaId,
            IntegrationHttp.ReadPath(message, "context.id"), sentAt, phoneNumberId);
    }

    private static string? FormatLocation(JsonElement message)
    {
        var latitude = IntegrationHttp.ReadPath(message, "location.latitude");
        var longitude = IntegrationHttp.ReadPath(message, "location.longitude");
        return latitude is null || longitude is null ? null : $"Location {latitude},{longitude}";
    }
}
