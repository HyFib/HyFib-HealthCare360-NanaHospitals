using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Integrations.Common;

/// <summary>Fields every <c>integrations.&lt;name&gt;</c> setting carries.</summary>
internal interface IIntegrationSettings
{
    bool Enabled { get; }

    string? Provider { get; }
}

/// <summary>Secret settings for integrations that have no credentials (Tally's XML port is unauthenticated).</summary>
internal sealed record NoSecrets;

/// <summary>The two fields every integration setting shares; enough for the administration dashboard.</summary>
internal sealed record IntegrationSummarySettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    public string? Provider { get; init; }
}

/// <summary>An integration's settings and decrypted secrets, or the staff-facing reason it cannot be used.</summary>
internal sealed record IntegrationConfig<TSettings, TSecrets>(TSettings? Settings, TSecrets Secrets, string? Problem)
    where TSettings : class, IIntegrationSettings
    where TSecrets : class
{
    /// <summary>Lower-case provider key when the integration is configured and enabled; otherwise null.</summary>
    public string? ActiveProvider => Problem is null && Settings is { Enabled: true } ? Settings.Provider?.Trim().ToLowerInvariant() : null;
}

/// <summary>
/// Reads per-facility integration configuration: non-secret <c>integrations.&lt;name&gt;</c> and encrypted
/// <c>integrations.&lt;name&gt;.secret</c>. The facility is the signed-in user's unless one is given (webhooks);
/// background work (the outbox) has no facility and therefore uses the global rows. Malformed or undecryptable
/// settings become a problem message instead of an exception, so a bad configuration fails one call, not the host. So
/// does the simulator on a hospital's live server (<see cref="TrainingSimulators"/>), whenever it was chosen.
/// </summary>
internal sealed class IntegrationSettingsReader(
    ISettingsService settings, ICurrentUser currentUser, IOptions<PlatformOptions> platform, ILogger<IntegrationSettingsReader> logger)
{
    public Task<IntegrationConfig<TSettings, TSecrets>> ReadAsync<TSettings, TSecrets>(string integration, CancellationToken cancellationToken)
        where TSettings : class, IIntegrationSettings
        where TSecrets : class, new() =>
        ReadAsync<TSettings, TSecrets>(integration, null, requireEnabled: true, cancellationToken);

    /// <param name="requireEnabled">
    /// False for inbound callbacks: delivery receipts for messages sent before an administrator disabled the
    /// integration must still be verified and applied.
    /// </param>
    public async Task<IntegrationConfig<TSettings, TSecrets>> ReadAsync<TSettings, TSecrets>(
        string integration, Guid? facilityId, bool requireEnabled, CancellationToken cancellationToken)
        where TSettings : class, IIntegrationSettings
        where TSecrets : class, new()
    {
        var facility = facilityId ?? currentUser.FacilityId;
        var display = IntegrationNames.DisplayName(integration);

        TSettings? values;
        try
        {
            values = await settings.GetAsync<TSettings>(IntegrationNames.SettingsKey(integration), facility, cancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Settings for integration {Integration} are not valid", integration);
            return new(null, new TSecrets(), $"The {display} settings are not valid. Correct them in Administration → Integrations.");
        }

        if (values is null)
        {
            return new(null, new TSecrets(), $"{display} is not configured. Set it up in Administration → Integrations.");
        }

        // Callbacks and reconciliation read it too: nothing a simulator answers is taken on a live server.
        if (!platform.Value.AllowsSimulators && TrainingSimulators.IsSimulator(values.Provider))
        {
            return new(values, new TSecrets(),
                $"{display} is set to the simulator, which only a training or test server can use. Choose the real provider in Administration → Integrations.");
        }

        if (requireEnabled && !values.Enabled)
        {
            return new(values, new TSecrets(), $"{display} is disabled.");
        }

        try
        {
            var secrets = await settings.GetAsync<TSecrets>(IntegrationNames.SecretKey(integration), facility, cancellationToken);
            return new(values, secrets ?? new TSecrets(), null);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            // Only the exception type is logged: messages of failed decrypt/parse operations must never echo a secret.
            logger.LogWarning("Secret settings for integration {Integration} could not be read ({ErrorType})", integration, ex.GetType().Name);
            return new(values, new TSecrets(), $"The {display} credentials could not be read. Re-enter them in Administration → Integrations.");
        }
    }

    /// <summary>
    /// Synchronous read for platform contracts that are synchronous (<c>IPaymentGateway.VerifySignature</c>,
    /// <c>IAccountingExporter.BuildImportFile</c>). Settings are memory-cached by the settings service for a minute,
    /// so this blocks on the database only on a cache miss; ASP.NET Core has no synchronisation context to deadlock.
    /// </summary>
    public IntegrationConfig<TSettings, TSecrets> Read<TSettings, TSecrets>(string integration, Guid? facilityId, bool requireEnabled)
        where TSettings : class, IIntegrationSettings
        where TSecrets : class, new() =>
        ReadAsync<TSettings, TSecrets>(integration, facilityId, requireEnabled, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>A plain (non-integration-shaped) setting such as the analyser list; invalid JSON reads as missing.</summary>
    public async Task<T?> ReadValueAsync<T>(string key, Guid? facilityId, CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetAsync<T>(key, facilityId, cancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Setting {Key} is not valid", key);
            return default;
        }
    }
}
