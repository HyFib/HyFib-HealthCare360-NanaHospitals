using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Integrations.Sms;

/// <summary>
/// Twilio Messages API (<c>POST /2010-04-01/Accounts/{sid}/Messages.json</c>, HTTP basic auth with the account SID
/// and auth token). Twilio's response echoes the message body and number, so only the SID/status/error are logged.
/// </summary>
internal sealed partial class TwilioSmsProvider(HttpClient http, IntegrationTrace trace) : ISmsProvider
{
    public string Key => ProviderKeys.Twilio;

    public async Task<SendResult> SendAsync(SmsMessage message, SmsSettings settings, SmsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var twilio = settings.Twilio;
        if (string.IsNullOrWhiteSpace(twilio.AccountSid) || !AccountSidPattern().IsMatch(twilio.AccountSid) || string.IsNullOrWhiteSpace(secrets.TwilioAuthToken))
        {
            return await FailAsync(message, started, "A valid Twilio account SID and auth token are required.");
        }

        if (!IntegrationHttp.TryCreateEndpoint(twilio.BaseUrl, requireHttps: true, out var baseUri))
        {
            return await FailAsync(message, started, "The Twilio base URL must be an https address.");
        }

        var form = new List<KeyValuePair<string, string>> { new("To", message.To), new("Body", message.Text) };
        if (!string.IsNullOrWhiteSpace(twilio.MessagingServiceSid))
        {
            form.Add(new("MessagingServiceSid", twilio.MessagingServiceSid));
        }
        else if ((twilio.From ?? message.SenderId) is { Length: > 0 } from)
        {
            form.Add(new("From", from));
        }
        else
        {
            return await FailAsync(message, started, "Configure a Twilio 'from' number or messaging service.");
        }

        if (!string.IsNullOrWhiteSpace(twilio.StatusCallbackUrl))
        {
            form.Add(new("StatusCallback", twilio.StatusCallbackUrl));
        }

        var uri = IntegrationHttp.Combine(baseUri, $"2010-04-01/Accounts/{Uri.EscapeDataString(twilio.AccountSid)}/Messages.json");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new FormUrlEncodedContent(form) };
        IntegrationHttp.SetBasicAuth(request, twilio.AccountSid, secrets.TwilioAuthToken);

        var summary = $"{{\"to\":\"{Phone.Mask(message.To)}\",\"length\":{message.Text.Length}}}";
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (success, sid, status, error) = ParseResponse(response.IsSuccessStatusCode, body);
            await trace.LogAsync(IntegrationNames.Sms, Key, "send", success, started, message.Reference, summary,
                $"HTTP {(int)response.StatusCode} sid={sid} status={status}", error);
            return success ? SendResult.Ok(sid) : SendResult.Fail(error!);
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "Twilio");
            await trace.LogAsync(IntegrationNames.Sms, Key, "send", false, started, message.Reference, summary, error: error);
            return SendResult.Fail(error);
        }
    }

    /// <summary>201 → <c>{"sid":"SM…","status":"queued"}</c>; errors → <c>{"code":21211,"message":"…"}</c>.</summary>
    internal static (bool Success, string? Sid, string? Status, string? Error) ParseResponse(bool httpSuccess, string body)
    {
        if (!IntegrationHttp.TryParseJson(body, out var document))
        {
            return (false, null, null, "Twilio returned an unreadable response.");
        }

        using (document)
        {
            var root = document.RootElement;
            var sid = IntegrationHttp.Property(root, "sid");
            if (httpSuccess && !string.IsNullOrEmpty(sid))
            {
                return (true, sid, IntegrationHttp.Property(root, "status"), null);
            }

            var code = IntegrationHttp.Property(root, "code");
            var text = IntegrationHttp.Property(root, "message") ?? "request failed";
            return (false, null, null, code is null ? $"Twilio: {text}" : $"Twilio error {code}: {text}");
        }
    }

    [GeneratedRegex("^AC[0-9a-fA-F]{32}$")]
    private static partial Regex AccountSidPattern();

    private async Task<SendResult> FailAsync(SmsMessage message, long started, string error)
    {
        await trace.LogAsync(IntegrationNames.Sms, Key, "send", false, started, message.Reference, error: error);
        return SendResult.Fail(error);
    }
}
