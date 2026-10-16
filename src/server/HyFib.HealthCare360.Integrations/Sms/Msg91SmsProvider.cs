namespace HyFib.HealthCare360.Integrations.Sms;

/// <summary>
/// MSG91 Flow API (<c>POST /api/v5/flow</c>, <c>authkey</c> header). Indian DLT rules require every SMS to match a
/// registered template, and the Flow API takes the template id plus its variable values rather than free text. HC360
/// renders notifications itself, so the variables are recovered by matching the rendered text against the HC360
/// template registered under the same DLT id; MSG91 variables must be named like the HC360 placeholders
/// (<c>##name##</c> for <c>{{name}}</c>).
/// </summary>
internal sealed class Msg91SmsProvider(HttpClient http, ISmsTemplateCatalog templates, IntegrationTrace trace) : ISmsProvider
{
    public string Key => ProviderKeys.Msg91;

    public async Task<SendResult> SendAsync(SmsMessage message, SmsSettings settings, SmsSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (string.IsNullOrWhiteSpace(secrets.Msg91AuthKey))
        {
            return await FailAsync(message, started, "The MSG91 auth key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(message.DltTemplateId))
        {
            return await FailAsync(message, started, "This SMS template has no DLT template id; MSG91 requires one.");
        }

        if (!IntegrationHttp.TryCreateEndpoint(settings.Msg91.BaseUrl, requireHttps: true, out var baseUri))
        {
            return await FailAsync(message, started, "The MSG91 base URL must be an https address.");
        }

        var variables = await ResolveVariablesAsync(message, settings.Msg91, cancellationToken);
        if (variables is null)
        {
            return await FailAsync(message, started,
                $"The SMS text does not match the HC360 template registered for DLT template {message.DltTemplateId}.");
        }

        var templateId = settings.Msg91.TemplateIds?.GetValueOrDefault(message.DltTemplateId) ?? message.DltTemplateId;
        var recipient = new Dictionary<string, string>(StringComparer.Ordinal) { ["mobiles"] = message.To.TrimStart('+') };
        foreach (var (name, value) in variables)
        {
            recipient.TryAdd(name, value);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, IntegrationHttp.Combine(baseUri, "api/v5/flow"))
        {
            Content = IntegrationHttp.JsonBody(new { template_id = templateId, short_url = settings.Msg91.ShortUrl ? "1" : "0", recipients = new[] { recipient } }),
        };
        request.Headers.TryAddWithoutValidation("authkey", secrets.Msg91AuthKey);
        request.Headers.Accept.ParseAdd("application/json");

        var summary = $"{{\"to\":\"{Phone.Mask(message.To)}\",\"templateId\":\"{templateId}\",\"variables\":{variables.Count}}}";
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var (success, requestId, error) = ParseResponse(response.IsSuccessStatusCode, body);
            await trace.LogAsync(IntegrationNames.Sms, Key, "send", success, started, message.Reference, summary,
                IntegrationHttp.Summarize(response, requestId), success ? null : error);
            return success ? SendResult.Ok(requestId) : SendResult.Fail(error!);
        }
        catch (Exception ex) when (IntegrationErrors.IsCallFailure(ex, cancellationToken))
        {
            var error = IntegrationErrors.Describe(ex, "MSG91");
            await trace.LogAsync(IntegrationNames.Sms, Key, "send", false, started, message.Reference, summary, error: error);
            return SendResult.Fail(error);
        }
    }

    /// <summary>MSG91 answers <c>{"type":"success","message":"&lt;request id&gt;"}</c> or <c>{"type":"error","message":"…"}</c>.</summary>
    internal static (bool Success, string? RequestId, string? Error) ParseResponse(bool httpSuccess, string body)
    {
        if (!IntegrationHttp.TryParseJson(body, out var document))
        {
            return (false, null, httpSuccess ? "MSG91 returned an unreadable response." : "MSG91 rejected the request.");
        }

        using (document)
        {
            var type = IntegrationHttp.Property(document.RootElement, "type");
            var text = IntegrationHttp.Property(document.RootElement, "message");
            return httpSuccess && string.Equals(type, "success", StringComparison.OrdinalIgnoreCase)
                ? (true, text, null)
                : (false, null, "MSG91: " + (text ?? "request failed"));
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> ResolveVariablesAsync(SmsMessage message, Msg91Options options, CancellationToken cancellationToken)
    {
        foreach (var body in await templates.BodiesForDltTemplateAsync(message.DltTemplateId!, cancellationToken))
        {
            if (TemplateRenderer.TryExtract(body, message.Text, out var values))
            {
                return values;
            }
        }

        return string.IsNullOrWhiteSpace(options.TextVariable)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { [options.TextVariable] = message.Text };
    }

    private async Task<SendResult> FailAsync(SmsMessage message, long started, string error)
    {
        await trace.LogAsync(IntegrationNames.Sms, Key, "send", false, started, message.Reference, error: error);
        return SendResult.Fail(error);
    }
}
