namespace HyFib.HealthCare360.Integrations.Sms;

/// <summary>
/// The platform's <see cref="ISmsSender"/>: reads the facility's SMS settings on every call and delegates to the
/// configured provider, so an administrator can switch gateways (or to the simulator) without a restart.
/// Configuration problems are returned as failures — never a silent success — and traced for the dashboard.
/// </summary>
internal sealed class SmsRouter(IEnumerable<ISmsProvider> providers, IntegrationSettingsReader settings, IntegrationTrace trace) : ISmsSender
{
    /// <summary>The provider is chosen per facility and call; see <c>integrations.sms</c>.</summary>
    public string Provider => "configured";

    public async Task<SendResult> SendAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        // Outbox sends have no signed-in user: the log rows count against the message's facility.
        using var facility = IntegrationTrace.ForFacility(message.FacilityId);
        var started = trace.Start();
        var config = await settings.ReadAsync<SmsSettings, SmsSecrets>(IntegrationNames.Sms, message.FacilityId, requireEnabled: true, cancellationToken);
        if (config.ActiveProvider is not { } key)
        {
            return await FailAsync(message, "none", started, config.Problem ?? "SMS is not available.");
        }

        var provider = providers.FirstOrDefault(p => p.Key == key);
        if (provider is null)
        {
            return await FailAsync(message, key, started, $"Unknown SMS provider '{key}'. Use msg91, twilio, generic or simulator.");
        }

        if (Phone.Normalize(message.To) is not { } to)
        {
            return await FailAsync(message, key, started, "The recipient is not a valid phone number.");
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return await FailAsync(message, key, started, "The message text is empty.");
        }

        var senderId = string.IsNullOrWhiteSpace(message.SenderId) ? config.Settings!.SenderId : message.SenderId;
        return await provider.SendAsync(message with { To = to, SenderId = senderId }, config.Settings!, config.Secrets, cancellationToken);
    }

    private async Task<SendResult> FailAsync(SmsMessage message, string provider, long started, string error)
    {
        await trace.LogAsync(IntegrationNames.Sms, provider, "send", false, started, message.Reference, error: error);
        return SendResult.Fail(error);
    }
}
