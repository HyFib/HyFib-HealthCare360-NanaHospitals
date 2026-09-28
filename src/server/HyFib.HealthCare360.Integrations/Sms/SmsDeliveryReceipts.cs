using System.Text;

namespace HyFib.HealthCare360.Integrations.Sms;

internal enum DeliveryOutcome
{
    Delivered = 0,
    Failed,
    Pending,
}

/// <summary>A provider's delivery report for one message, keyed by the id the provider returned when sending.</summary>
internal sealed record DeliveryReceipt(string ProviderMessageId, DeliveryOutcome Outcome, string? Detail);

/// <summary>Parses delivery-receipt (DLR) callbacks from the SMS providers into <see cref="DeliveryReceipt"/>s.</summary>
internal static class SmsDeliveryReceipts
{
    /// <summary>
    /// MSG91 posts an array of <c>{requestId, report:[{status, desc, number}]}</c> (sometimes wrapped in
    /// <c>{"data": …}</c> or sent as a form field <c>data</c>). Status 1 is delivered; submitted/pending codes leave
    /// the message as sent; everything else is a failure with MSG91's description.
    /// </summary>
    public static IReadOnlyList<DeliveryReceipt> ParseMsg91(string json)
    {
        if (!IntegrationHttp.TryParseJson(json, out var document))
        {
            return [];
        }

        using (document)
        {
            var receipts = new List<DeliveryReceipt>();
            foreach (var item in Items(document.RootElement))
            {
                var requestId = IntegrationHttp.Property(item, "requestId") ?? IntegrationHttp.Property(item, "request_id");
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    continue;
                }

                var report = item.TryGetProperty("report", out var reports) && reports.ValueKind == JsonValueKind.Array && reports.GetArrayLength() > 0
                    ? reports[0]
                    : item;
                var status = IntegrationHttp.Property(report, "status");
                var description = IntegrationHttp.Property(report, "desc") ?? IntegrationHttp.Property(report, "description");
                var outcome = Msg91Outcome(status, description);
                receipts.Add(new DeliveryReceipt(requestId, outcome, outcome == DeliveryOutcome.Failed ? description ?? $"status {status}" : description));
            }

            return receipts;
        }
    }

    internal static DeliveryOutcome Msg91Outcome(string? status, string? description)
    {
        var text = description?.Trim().ToUpperInvariant() ?? string.Empty;
        string[] failureWords = ["UNDELIV", "FAIL", "REJECT", "BLOCK", "NDNC", "EXPIRED"];
        if (failureWords.Any(word => text.Contains(word, StringComparison.Ordinal)))
        {
            return DeliveryOutcome.Failed;
        }

        if (status == "1" || text.Contains("DELIVERED", StringComparison.Ordinal))
        {
            return DeliveryOutcome.Delivered;
        }

        string[] pendingWords = ["SUBMIT", "PENDING", "SENT", "QUEUED"];
        if (status == "8" || pendingWords.Any(word => text.Contains(word, StringComparison.Ordinal)))
        {
            return DeliveryOutcome.Pending;
        }

        // No status at all is not evidence of failure; anything else MSG91 reports (2, 9, 16, 17, 25, 26 …) is.
        return string.IsNullOrWhiteSpace(status) && text.Length == 0 ? DeliveryOutcome.Pending : DeliveryOutcome.Failed;
    }

    /// <summary>Twilio status callback form fields: <c>MessageSid</c>, <c>MessageStatus</c>, <c>ErrorCode</c>.</summary>
    public static DeliveryReceipt? ParseTwilio(IReadOnlyDictionary<string, string> form)
    {
        var sid = form.GetValueOrDefault("MessageSid") ?? form.GetValueOrDefault("SmsSid");
        var status = (form.GetValueOrDefault("MessageStatus") ?? form.GetValueOrDefault("SmsStatus"))?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var outcome = status switch
        {
            "delivered" or "read" => DeliveryOutcome.Delivered,
            "undelivered" or "failed" or "canceled" => DeliveryOutcome.Failed,
            _ => DeliveryOutcome.Pending,
        };
        var errorCode = form.GetValueOrDefault("ErrorCode");
        return new DeliveryReceipt(sid, outcome, outcome == DeliveryOutcome.Failed ? $"Twilio {status}{(string.IsNullOrEmpty(errorCode) ? string.Empty : " (error " + errorCode + ")")}" : status);
    }

    /// <summary>Generic gateways: id in <c>messageId</c>/<c>id</c>/<c>msgid</c>, state in <c>status</c>/<c>state</c>.</summary>
    public static DeliveryReceipt? ParseGeneric(IReadOnlyDictionary<string, string> fields)
    {
        var id = First(fields, "messageId", "message_id", "msgid", "id", "requestId");
        var status = First(fields, "status", "state", "dlr_status", "deliveryStatus")?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var outcome = status switch
        {
            "delivered" or "delivrd" or "dlvrd" or "success" or "1" => DeliveryOutcome.Delivered,
            "failed" or "undelivered" or "undeliv" or "rejected" or "expired" or "error" or "2" => DeliveryOutcome.Failed,
            _ => DeliveryOutcome.Pending,
        };
        return new DeliveryReceipt(id, outcome, status);
    }

    /// <summary>
    /// Twilio request signature: Base64(HMAC-SHA1(authToken, url + each POST parameter name and value, sorted by name
    /// ordinally)), as documented for <c>X-Twilio-Signature</c>.
    /// </summary>
    public static string TwilioSignaturePayload(string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        var payload = new StringBuilder(url);
        foreach (var (name, value) in form.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            payload.Append(name).Append(value);
        }

        return payload.ToString();
    }

    private static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
        {
            root = data;
        }

        if (root.ValueKind == JsonValueKind.String && IntegrationHttp.TryParseJson(root.GetString() ?? string.Empty, out var nested))
        {
            // Some MSG91 accounts double-encode the payload in the "data" field.
            using (nested)
            {
                return Items(nested.RootElement.Clone()).ToList();
            }
        }

        return root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => [root],
            _ => [],
        };
    }

    private static string? First(IReadOnlyDictionary<string, string> fields, params string[] names)
    {
        foreach (var name in names)
        {
            var match = fields.FirstOrDefault(f => string.Equals(f.Key, name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value))
            {
                return match.Value;
            }
        }

        return null;
    }
}
