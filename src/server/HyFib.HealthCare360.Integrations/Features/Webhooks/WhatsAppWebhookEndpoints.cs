using System.Text;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Integrations.WhatsApp;

namespace HyFib.HealthCare360.Integrations.Features.Webhooks;

/// <summary>
/// /api/v1/integrations/webhooks/whatsapp — Meta Cloud API webhook (INT-WA). GET answers the subscription check only
/// when <c>hub.verify_token</c> matches the configured token; POST bodies must carry a valid
/// <c>X-Hub-Signature-256</c> (HMAC-SHA256 of the raw body with the app secret). Delivery statuses update the
/// notification log; patient messages are stored with opt-in/opt-out keywords.
/// </summary>
internal static partial class WhatsAppWebhookEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/integrations/webhooks/whatsapp")
            .WithTags("Integration webhooks")
            .AllowAnonymous()
            .RequireRateLimiting(WebhookRequests.RateLimitPolicy);

        group.MapGet("/", Verify);
        group.MapPost("/", Receive);
    }

    private static async Task<IResult> Verify(HttpContext context, IntegrationSettingsReader settings, CancellationToken ct)
    {
        var query = context.Request.Query;
        var config = await settings.ReadAsync<WhatsAppSettings, WhatsAppSecrets>(IntegrationNames.WhatsApp, WebhookRequests.Facility(context.Request), requireEnabled: false, ct);
        var challenge = query["hub.challenge"].ToString();
        var valid = query["hub.mode"].ToString() == "subscribe"
                    && Signatures.TokensEqual(config.Secrets.VerifyToken, query["hub.verify_token"].ToString())
                    && ChallengePattern().IsMatch(challenge);
        return valid ? TypedResults.Text(challenge, "text/plain") : TypedResults.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> Receive(
        HttpContext context, IntegrationSettingsReader settings, NotificationLogUpdater logs, WhatsAppInboundStore inbound, IntegrationTrace trace, CancellationToken ct)
    {
        var started = trace.Start();
        var facility = WebhookRequests.Facility(context.Request);
        var config = await settings.ReadAsync<WhatsAppSettings, WhatsAppSecrets>(IntegrationNames.WhatsApp, facility, requireEnabled: false, ct);
        var body = await WebhookRequests.ReadBodyAsync(context.Request, ct);
        var signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
        var hex = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? signature[7..] : null;
        if (body is null || !Signatures.VerifyHmacSha256Hex(config.Secrets.AppSecret, body, hex))
        {
            await trace.LogAsync(IntegrationNames.WhatsApp, ProviderKeys.MetaCloud, "webhook", false, started,
                error: "Webhook with a missing or invalid X-Hub-Signature-256.", direction: IntegrationDirection.Inbound);
            return TypedResults.Unauthorized();
        }

        var batch = WhatsAppWebhookParser.Parse(Encoding.UTF8.GetString(body));
        var updated = await logs.ApplyAsync(batch.Statuses, ct);
        var stored = await inbound.SaveAsync(batch.Messages, facility, ct);
        await trace.LogAsync(IntegrationNames.WhatsApp, ProviderKeys.MetaCloud, "webhook", true, started, batch.Statuses.FirstOrDefault()?.ProviderMessageId,
            $"{batch.Statuses.Count} status(es), {batch.Messages.Count} message(s)", $"{updated} notification(s) updated, {stored} message(s) stored",
            direction: IntegrationDirection.Inbound);

        // Meta retries anything but 200, so a well-formed but irrelevant payload is still acknowledged.
        return TypedResults.Ok();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ChallengePattern();
}
