namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>Non-secret WhatsApp configuration (<c>integrations.whatsapp</c>) for the Meta WhatsApp Cloud API (INT-WA).</summary>
internal sealed record WhatsAppSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>meta</c> or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    public string GraphBaseUrl { get; init; } = "https://graph.facebook.com";

    public string ApiVersion { get; init; } = "v21.0";

    /// <summary>The business phone number id (not the phone number) from WhatsApp Manager.</summary>
    public string? PhoneNumberId { get; init; }

    public string? BusinessAccountId { get; init; }

    /// <summary>HC360 language → template language code, for example <c>{"en":"en_US","hi":"hi"}</c>.</summary>
    public IReadOnlyDictionary<string, string>? LanguageCodes { get; init; }

    public int MaxDocumentMegabytes { get; init; } = 20;

    /// <summary>Template used by the administrator's "send test" (Meta's sample template needs no parameters).</summary>
    public string TestTemplateName { get; init; } = "hello_world";

    public string TestTemplateLanguage { get; init; } = "en_US";
}

/// <summary>Secret WhatsApp configuration (<c>integrations.whatsapp.secret</c>).</summary>
internal sealed record WhatsAppSecrets
{
    /// <summary>System-user access token with whatsapp_business_messaging permission.</summary>
    public string? AccessToken { get; init; }

    /// <summary>App secret used to verify <c>X-Hub-Signature-256</c> on webhook POSTs.</summary>
    public string? AppSecret { get; init; }

    /// <summary>Token echoed by Meta in the webhook verification GET (<c>hub.verify_token</c>).</summary>
    public string? VerifyToken { get; init; }
}

/// <summary>One WhatsApp provider; the router picks it by <see cref="Key"/>.</summary>
internal interface IWhatsAppProvider
{
    string Key { get; }

    Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken);

    Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken);

    Task<SendResult> SendTextAsync(WhatsAppTextMessage message, WhatsAppSettings settings, WhatsAppSecrets secrets, CancellationToken cancellationToken);
}
