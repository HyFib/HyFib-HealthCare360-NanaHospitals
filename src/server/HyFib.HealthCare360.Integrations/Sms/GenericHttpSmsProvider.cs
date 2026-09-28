using System.Text;

namespace HyFib.HealthCare360.Integrations.Sms;

/// <summary>
/// Any HTTP SMS gateway described by URL/body templates (many Indian gateways: query-string APIs with the key in the
/// URL, or JSON APIs with a header key). Only the URL <em>template</em> is logged — the rendered URL can carry the key.
/// </summary>
internal sealed class GenericHttpSmsProvider(HttpClient http, IntegrationTrace trace) : ISmsProvider
{
    public string Key => ProviderKeys.GenericHttp;

    public async Task<SendResult> SendAsync(SmsMessage message, SmsSettings settings, SmsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var generic = settings.Generic;
        if (string.IsNullOrWhiteSpace(generic.Url))
        {
            return await FailAsync(message, started, "The generic SMS gateway URL is not configured.");
        }

        var method = generic.Method.Trim().ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            _ => null,
        };
        if (method is null)
        {
            return await FailAsync(message, started, "The generic SMS gateway method must be GET, POST or PUT.");
        }

        var values = Values(message, settings, secrets);
        if (!IntegrationHttp.TryCreateEndpoint(TemplateRenderer.Render(generic.Url, values, TemplateEncoding.Url), requireHttps: false, out var uri))
        {
            return await FailAsync(message, started, "The generic SMS gateway URL is not a valid http(s) address.");
        }

        using var request = new HttpRequestMessage(method, uri);
        if (method != HttpMethod.Get && !string.IsNullOrWhiteSpace(generic.BodyTemplate))
        {
            var mediaType = string.IsNullOrWhiteSpace(generic.ContentType) ? "application/json" : generic.ContentType.Trim();
            var encoding = mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ? TemplateEncoding.Json
                : mediaType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ? TemplateEncoding.Url
                : TemplateEncoding.None;
            request.Content = new StringContent(TemplateRenderer.Render(generic.BodyTemplate, values, encoding), Encoding.UTF8, mediaType);
        }

        foreach (var (name, value) in generic.Headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (!string.IsNullOrWhiteSpace(generic.ApiKeyHeader) && !string.IsNullOrWhiteSpace(secrets.GenericApiKey))
        {
            request.Headers.TryAddWithoutValidation(generic.ApiKeyHeader, (generic.ApiKeyPrefix ?? string.Empty) + secrets.GenericApiKey);
        }

        var summary = $"{{\"url\":\"{generic.Url}\",\"to\":\"{Phone.Mask(message.To)}\",\"length\":{message.Text.Length}}}";
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (success, id, error) = Evaluate(generic, response.IsSuccessStatusCode, (int)response.StatusCode, body);
            await trace.LogAsync(IntegrationNames.Sms, Key, "send", success, started, message.Reference, summary,
                IntegrationHttp.Summarize(response, body), error);
            return success ? SendResult.Ok(id) : SendResult.Fail(error!);
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "the SMS gateway");
            await trace.LogAsync(IntegrationNames.Sms, Key, "send", false, started, message.Reference, summary, error: error);
            return SendResult.Fail(error);
        }
    }

    internal static Dictionary<string, string?> Values(SmsMessage message, SmsSettings settings, SmsSecrets secrets)
    {
        var digits = message.To.TrimStart('+');
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["to"] = message.To,
            ["toDigits"] = digits,
            ["to10"] = digits.Length > 10 ? digits[^10..] : digits,
            ["text"] = message.Text,
            ["dltTemplateId"] = message.DltTemplateId,
            ["dltEntityId"] = settings.DltEntityId,
            ["senderId"] = message.SenderId ?? settings.SenderId,
            ["reference"] = message.Reference,
            ["apiKey"] = secrets.GenericApiKey,
        };
    }

    internal static (bool Success, string? MessageId, string? Error) Evaluate(GenericHttpSmsOptions options, bool httpSuccess, int status, string body)
    {
        if (!httpSuccess)
        {
            return (false, null, $"The SMS gateway returned HTTP {status}.");
        }

        if (!string.IsNullOrEmpty(options.SuccessContains) && !body.Contains(options.SuccessContains, StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "The SMS gateway did not confirm the message: " + IntegrationTrace.Truncate(body, 200));
        }

        string? id = null;
        if (!string.IsNullOrWhiteSpace(options.MessageIdPath) && IntegrationHttp.TryParseJson(body, out var document))
        {
            using (document)
            {
                id = IntegrationHttp.ReadPath(document.RootElement, options.MessageIdPath);
            }
        }

        return (true, id, null);
    }

    private async Task<SendResult> FailAsync(SmsMessage message, long started, string error)
    {
        await trace.LogAsync(IntegrationNames.Sms, Key, "send", false, started, message.Reference, error: error);
        return SendResult.Fail(error);
    }
}
