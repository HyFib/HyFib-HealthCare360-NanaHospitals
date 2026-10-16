namespace HyFib.HealthCare360.Integrations.Email;

/// <summary>The platform's <see cref="IEmailSender"/>: resolves the facility's e-mail provider (SMTP or simulator) per call.</summary>
internal sealed class EmailRouter(IEnumerable<IEmailProvider> providers, IntegrationSettingsReader settings, IntegrationTrace trace) : IEmailSender
{
    public async Task<SendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        // Outbox sends have no signed-in user: the log rows count against the message's facility.
        using var facility = IntegrationTrace.ForFacility(message.FacilityId);
        var started = trace.Start();
        var config = await settings.ReadAsync<EmailSettings, EmailSecrets>(IntegrationNames.Email, message.FacilityId, requireEnabled: true, cancellationToken);
        string? error = null;
        IEmailProvider? provider = null;
        if (config.ActiveProvider is not { } key)
        {
            error = config.Problem ?? "E-mail is not available.";
        }
        else if ((provider = providers.FirstOrDefault(p => p.Key == key)) is null)
        {
            error = $"Unknown e-mail provider '{key}'. Use smtp or simulator.";
        }
        else if (string.IsNullOrWhiteSpace(message.To))
        {
            error = "The recipient e-mail address is missing.";
        }

        if (error is not null || provider is null)
        {
            await trace.LogAsync(IntegrationNames.Email, config.ActiveProvider ?? "none", "send", false, started, message.Reference, error: error);
            return SendResult.Fail(error ?? "E-mail is not available.");
        }

        return await provider.SendAsync(message, config.Settings!, config.Secrets, cancellationToken);
    }
}
