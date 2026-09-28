namespace HyFib.HealthCare360.Integrations.Sms;

/// <summary>Non-secret SMS configuration (<c>integrations.sms</c>). One provider is active per facility (INT-SMS).</summary>
internal sealed record SmsSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>msg91</c>, <c>twilio</c>, <c>generic</c> or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>TRAI-registered sender id (header) used when a message does not name one.</summary>
    public string? SenderId { get; init; }

    /// <summary>TRAI DLT principal entity id, for gateways that expect it on every request.</summary>
    public string? DltEntityId { get; init; }

    public Msg91Options Msg91 { get; init; } = new();

    public TwilioOptions Twilio { get; init; } = new();

    public GenericHttpSmsOptions Generic { get; init; } = new();

    /// <summary>Optional IP allow-list for delivery-receipt callbacks, checked in addition to the token/signature.</summary>
    public IReadOnlyList<string>? WebhookAllowedIps { get; init; }
}

internal sealed record Msg91Options
{
    public string BaseUrl { get; init; } = "https://control.msg91.com";

    /// <summary>DLT template id → MSG91 template id, where MSG91's id differs from the DLT id.</summary>
    public IReadOnlyDictionary<string, string>? TemplateIds { get; init; }

    /// <summary>
    /// Fallback when the SMS text cannot be matched to its HC360 template: send the whole text in this MSG91 variable
    /// (the MSG91 template must then consist of that single variable). Leave empty to fail instead.
    /// </summary>
    public string? TextVariable { get; init; }

    public bool ShortUrl { get; init; }
}

internal sealed record TwilioOptions
{
    public string BaseUrl { get; init; } = "https://api.twilio.com";

    public string? AccountSid { get; init; }

    public string? From { get; init; }

    public string? MessagingServiceSid { get; init; }

    /// <summary>Public URL of HC360's Twilio status webhook exactly as Twilio calls it (signature validation signs it).</summary>
    public string? StatusCallbackUrl { get; init; }
}

/// <summary>
/// Any HTTP SMS gateway. <see cref="Url"/> and <see cref="BodyTemplate"/> accept placeholders: <c>{{to}}</c> (E.164),
/// <c>{{toDigits}}</c>, <c>{{to10}}</c>, <c>{{text}}</c>, <c>{{dltTemplateId}}</c>, <c>{{dltEntityId}}</c>,
/// <c>{{senderId}}</c>, <c>{{reference}}</c> and <c>{{apiKey}}</c> (from the secret settings).
/// </summary>
internal sealed record GenericHttpSmsOptions
{
    public string? Url { get; init; }

    public string Method { get; init; } = "POST";

    public string ContentType { get; init; } = "application/json";

    public string? BodyTemplate { get; init; }

    /// <summary>Extra non-secret headers.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Header that carries the API key (for example <c>Authorization</c> or <c>x-api-key</c>).</summary>
    public string? ApiKeyHeader { get; init; }

    /// <summary>Prefix for the API key header value (for example <c>Bearer </c>).</summary>
    public string? ApiKeyPrefix { get; init; }

    /// <summary>Dotted path of the provider's message id in a JSON response (for example <c>data.id</c>).</summary>
    public string? MessageIdPath { get; init; }

    /// <summary>Text a successful response body must contain (for gateways that answer 200 on errors).</summary>
    public string? SuccessContains { get; init; }
}

/// <summary>Secret SMS configuration (<c>integrations.sms.secret</c>), stored encrypted.</summary>
internal sealed record SmsSecrets
{
    public string? Msg91AuthKey { get; init; }

    public string? TwilioAuthToken { get; init; }

    public string? GenericApiKey { get; init; }

    /// <summary>Shared token MSG91/generic delivery-receipt callbacks must present (<c>?token=…</c>).</summary>
    public string? WebhookToken { get; init; }
}

/// <summary>One SMS gateway adapter; the router picks it by <see cref="Key"/>.</summary>
internal interface ISmsProvider
{
    string Key { get; }

    Task<SendResult> SendAsync(SmsMessage message, SmsSettings settings, SmsSecrets secrets, CancellationToken cancellationToken);
}
