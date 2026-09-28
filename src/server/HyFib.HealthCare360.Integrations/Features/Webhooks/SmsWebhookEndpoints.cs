using System.Text;
using HyFib.HealthCare360.Integrations.Sms;

namespace HyFib.HealthCare360.Integrations.Features.Webhooks;

/// <summary>
/// /api/v1/integrations/webhooks/sms/* — delivery receipts (INT-SMS). MSG91 and generic gateways cannot sign callbacks,
/// so they must present the shared <c>webhookToken</c> (<c>?token=…</c>, constant-time compare, optionally plus an IP
/// allow-list); Twilio callbacks are verified with <c>X-Twilio-Signature</c>. Unverified calls get 401 and change nothing.
/// </summary>
internal static class SmsWebhookEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations/webhooks/sms")
            .WithTags("Integration webhooks")
            .AllowAnonymous()
            .RequireRateLimiting(WebhookRequests.RateLimitPolicy);

        group.MapPost("/msg91", Msg91);
        group.MapPost("/twilio", Twilio);
        group.MapMethods("/generic", [HttpMethods.Get, HttpMethods.Post], Generic);
    }

    private static async Task<IResult> Msg91(HttpContext context, IntegrationSettingsReader settings, NotificationLogUpdater logs, IntegrationTrace trace, CancellationToken ct)
    {
        var started = trace.Start();
        var config = await settings.ReadAsync<SmsSettings, SmsSecrets>(IntegrationNames.Sms, WebhookRequests.Facility(context.Request), requireEnabled: false, ct);
        if (!Authorized(context, config))
        {
            return await RejectAsync(trace, ProviderKeys.Msg91, started, "Delivery receipt with an invalid token or from a disallowed address.");
        }

        string? json;
        if (context.Request.HasFormContentType)
        {
            var fields = await WebhookRequests.ReadFieldsAsync(context.Request, ct);
            json = fields?.GetValueOrDefault("data");
        }
        else
        {
            var body = await WebhookRequests.ReadBodyAsync(context.Request, ct);
            json = body is null ? null : Encoding.UTF8.GetString(body);
        }

        if (json is null)
        {
            return TypedResults.BadRequest();
        }

        var receipts = SmsDeliveryReceipts.ParseMsg91(json);
        var updated = await logs.ApplyAsync(receipts, ct);
        await trace.LogAsync(IntegrationNames.Sms, ProviderKeys.Msg91, "receipt", true, started, receipts.FirstOrDefault()?.ProviderMessageId,
            $"{receipts.Count} receipt(s)", $"{updated} notification(s) updated", direction: IntegrationDirection.Inbound);
        return TypedResults.Ok(new { received = receipts.Count, updated });
    }

    private static async Task<IResult> Twilio(HttpContext context, IntegrationSettingsReader settings, NotificationLogUpdater logs, IntegrationTrace trace, CancellationToken ct)
    {
        var started = trace.Start();
        var config = await settings.ReadAsync<SmsSettings, SmsSecrets>(IntegrationNames.Sms, WebhookRequests.Facility(context.Request), requireEnabled: false, ct);
        var form = context.Request.HasFormContentType ? await WebhookRequests.ReadFieldsAsync(context.Request, ct) : null;
        if (form is null || config.Settings is null)
        {
            return await RejectAsync(trace, ProviderKeys.Twilio, started, "Status callback without form data or Twilio configuration.");
        }

        // Twilio signs the exact URL it called plus the POSTed fields (not the query string fields a second time).
        var url = string.IsNullOrWhiteSpace(config.Settings.Twilio.StatusCallbackUrl) ? WebhookRequests.RequestUrl(context.Request) : config.Settings.Twilio.StatusCallbackUrl;
        var posted = context.Request.Form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value.ToString()));
        var signature = context.Request.Headers["X-Twilio-Signature"].ToString();
        if (!Signatures.VerifyHmacSha1Base64(config.Secrets.TwilioAuthToken, SmsDeliveryReceipts.TwilioSignaturePayload(url, posted), signature)
            || !WebhookRequests.IsAllowed(context, config.Settings.WebhookAllowedIps))
        {
            return await RejectAsync(trace, ProviderKeys.Twilio, started, "Status callback with an invalid X-Twilio-Signature.");
        }

        var receipt = SmsDeliveryReceipts.ParseTwilio(form);
        var updated = receipt is null ? 0 : await logs.ApplyAsync([receipt], ct);
        await trace.LogAsync(IntegrationNames.Sms, ProviderKeys.Twilio, "receipt", receipt is not null, started, receipt?.ProviderMessageId,
            receipt?.Outcome.ToString(), $"{updated} notification(s) updated", receipt is null ? "Unrecognised status callback." : null, IntegrationDirection.Inbound);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Generic(HttpContext context, IntegrationSettingsReader settings, NotificationLogUpdater logs, IntegrationTrace trace, CancellationToken ct)
    {
        var started = trace.Start();
        var config = await settings.ReadAsync<SmsSettings, SmsSecrets>(IntegrationNames.Sms, WebhookRequests.Facility(context.Request), requireEnabled: false, ct);
        if (!Authorized(context, config))
        {
            return await RejectAsync(trace, ProviderKeys.GenericHttp, started, "Delivery receipt with an invalid token or from a disallowed address.");
        }

        Dictionary<string, string>? fields;
        if (context.Request.HasJsonContentType())
        {
            var body = await WebhookRequests.ReadBodyAsync(context.Request, ct);
            fields = body is null ? null : JsonFields(Encoding.UTF8.GetString(body));
        }
        else
        {
            fields = await WebhookRequests.ReadFieldsAsync(context.Request, ct);
        }

        var receipt = fields is null ? null : SmsDeliveryReceipts.ParseGeneric(fields);
        if (receipt is null)
        {
            return TypedResults.BadRequest();
        }

        var updated = await logs.ApplyAsync([receipt], ct);
        await trace.LogAsync(IntegrationNames.Sms, ProviderKeys.GenericHttp, "receipt", true, started, receipt.ProviderMessageId,
            receipt.Outcome.ToString(), $"{updated} notification(s) updated", direction: IntegrationDirection.Inbound);
        return TypedResults.Ok(new { updated });
    }

    private static bool Authorized(HttpContext context, IntegrationConfig<SmsSettings, SmsSecrets> config) =>
        config.Settings is not null
        && Signatures.TokensEqual(config.Secrets.WebhookToken, context.Request.Query["token"].ToString())
        && WebhookRequests.IsAllowed(context, config.Settings.WebhookAllowedIps);

    private static Dictionary<string, string>? JsonFields(string json)
    {
        if (!IntegrationHttp.TryParseJson(json, out var document))
        {
            return null;
        }

        using (document)
        {
            return document.RootElement.ValueKind != JsonValueKind.Object
                ? null
                : document.RootElement.EnumerateObject()
                    .Select(p => (p.Name, Value: IntegrationHttp.Text(p.Value)))
                    .Where(p => p.Value is not null)
                    .ToDictionary(p => p.Name, p => p.Value!, StringComparer.Ordinal);
        }
    }

    private static async Task<IResult> RejectAsync(IntegrationTrace trace, string provider, long started, string reason)
    {
        await trace.LogAsync(IntegrationNames.Sms, provider, "receipt", false, started, error: reason, direction: IntegrationDirection.Inbound);
        return TypedResults.Unauthorized();
    }
}
