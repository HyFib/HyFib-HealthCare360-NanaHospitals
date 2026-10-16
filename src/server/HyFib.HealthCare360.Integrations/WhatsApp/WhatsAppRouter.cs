namespace HyFib.HealthCare360.Integrations.WhatsApp;

/// <summary>
/// The platform's <see cref="IWhatsAppSender"/>: resolves the facility's WhatsApp provider per call and fails clearly
/// (never silently) when WhatsApp is not configured.
/// </summary>
internal sealed class WhatsAppRouter(IEnumerable<IWhatsAppProvider> providers, IntegrationSettingsReader settings, IntegrationTrace trace) : IWhatsAppSender
{
    /// <summary>The Cloud API's limit for a text message body.</summary>
    public const int MaxTextLength = 4096;

    public string Provider => "configured";

    public async Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken)
    {
        // Outbox sends have no signed-in user: the log rows count against the message's facility.
        using var facility = IntegrationTrace.ForFacility(message.FacilityId);
        var started = trace.Start();
        var (provider, config, error) = await ResolveAsync(message.To, message.FacilityId, cancellationToken);
        if (provider is null)
        {
            return await FailAsync("template", message.Reference, started, error!);
        }

        if (string.IsNullOrWhiteSpace(message.TemplateName))
        {
            return await FailAsync("template", message.Reference, started, "A WhatsApp template name is required.");
        }

        return await provider.SendTemplateAsync(message with { To = Phone.Normalize(message.To)! }, config!.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(message.FacilityId);
        var started = trace.Start();
        var (provider, config, error) = await ResolveAsync(message.To, message.FacilityId, cancellationToken);
        if (provider is null)
        {
            return await FailAsync("document", message.Reference, started, error!);
        }

        if (message.Content.Length == 0)
        {
            return await FailAsync("document", message.Reference, started, "The document is empty.");
        }

        return await provider.SendDocumentAsync(message with { To = Phone.Normalize(message.To)! }, config!.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<SendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(message.FacilityId);
        var started = trace.Start();
        var (provider, config, error) = await ResolveAsync(message.To, message.FacilityId, cancellationToken);
        if (provider is null)
        {
            return await FailAsync("text", message.Reference, started, error!);
        }

        if (string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > MaxTextLength)
        {
            return await FailAsync("text", message.Reference, started, $"A WhatsApp text must have 1 to {MaxTextLength} characters.");
        }

        return await provider.SendTextAsync(message with { To = Phone.Normalize(message.To)! }, config!.Settings!, config.Secrets, cancellationToken);
    }

    private async Task<(IWhatsAppProvider? Provider, IntegrationConfig<WhatsAppSettings, WhatsAppSecrets>? Config, string? Error)> ResolveAsync(
        string to, Guid? facilityId, CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<WhatsAppSettings, WhatsAppSecrets>(IntegrationNames.WhatsApp, facilityId, requireEnabled: true, cancellationToken);
        if (config.ActiveProvider is not { } key)
        {
            return (null, null, config.Problem ?? "WhatsApp is not available.");
        }

        var provider = providers.FirstOrDefault(p => p.Key == key);
        if (provider is null)
        {
            return (null, null, $"Unknown WhatsApp provider '{key}'. Use meta or simulator.");
        }

        return Phone.Normalize(to) is null ? (null, null, "The recipient is not a valid phone number.") : (provider, config, null);
    }

    private async Task<SendResult> FailAsync(string operation, string? reference, long started, string error)
    {
        await trace.LogAsync(IntegrationNames.WhatsApp, "none", operation, false, started, reference, error: error);
        return SendResult.Fail(error);
    }
}
