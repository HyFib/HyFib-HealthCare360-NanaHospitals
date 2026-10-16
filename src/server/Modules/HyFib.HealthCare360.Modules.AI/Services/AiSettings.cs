using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>Setting keys (per facility, falling back to global; <c>*.secret</c> keys are stored encrypted).</summary>
internal static class AiSettingKeys
{
    /// <summary><c>true</c> to allow AI calls. Default false: AI is off until an administrator enables it (SEC-12).</summary>
    public const string Enabled = "ai.enabled";

    /// <summary><c>anthropic</c> (default) or <c>disabled</c>.</summary>
    public const string Provider = "ai.provider";

    public const string Model = "ai.model";
    public const string TimeoutSeconds = "ai.timeoutSeconds";

    /// <summary>The Anthropic API key: a JSON string, or <c>{"apiKey":"…"}</c>.</summary>
    public const string AnthropicSecret = "ai.anthropic.secret";

    public const string Speech = "ai.speech";
    public const string SpeechSecret = "ai.speech.secret";
    public const string Limits = "ai.limits";
}

internal static class AiProviders
{
    public const string Anthropic = "anthropic";
    public const string None = "none";
    public const string Whisper = "whisper";
}

/// <summary>Resolved AI text settings for the current facility.</summary>
internal sealed record AiTextSettings(bool Enabled, string Provider, string Model, string? ApiKey, TimeSpan Timeout)
{
    public bool IsUsable => Reason is null;

    public string? Reason =>
        !Enabled ? "AI features are turned off for this facility (setting ai.enabled)."
        : Provider != AiProviders.Anthropic ? $"AI provider '{Provider}' is not available."
        : string.IsNullOrWhiteSpace(ApiKey) ? "No Anthropic API key is configured (secret setting ai.anthropic.secret)."
        : null;
}

/// <summary>
/// Server speech-to-text (<c>ai.speech</c>): <c>none</c> (default — HC360 Desktop uses offline Windows speech) or
/// <c>whisper</c>, an OpenAI-compatible transcription endpoint such as an on-prem faster-whisper server. Dictation audio
/// is PHI, so it is sent only while <c>ai.enabled</c> is on, as for the text service (SEC-12), and only over https.
/// </summary>
internal sealed record SpeechSettings
{
    public string Provider { get; init; } = AiProviders.None;

    /// <summary>
    /// Full transcription URL, for example <c>https://stt.lan:8000/v1/audio/transcriptions</c>. Plain http is accepted only
    /// for a server on this machine (<c>localhost</c>), where the audio never crosses the network.
    /// </summary>
    public string? Url { get; init; }

    public string Model { get; init; } = "whisper-1";

    public string Language { get; init; } = "en";

    public int TimeoutSeconds { get; init; } = 120;
}

internal sealed record SpeechConfig(SpeechSettings Settings, Uri? Endpoint, string? ApiKey, bool AiEnabled)
{
    public bool IsUsable => Reason is null;

    public string? Reason =>
        !string.Equals(Settings.Provider, AiProviders.Whisper, StringComparison.OrdinalIgnoreCase)
            ? "Server speech-to-text is not configured; HC360 Desktop dictates with offline Windows speech."
            : !AiEnabled ? "AI features are turned off for this facility (setting ai.enabled)."
            : Endpoint is null ? "The speech-to-text URL (ai.speech.url) must be an https address without a user name or password (plain http only to localhost)."
            : null;

    /// <summary>
    /// The transcription endpoint, when <paramref name="url"/> is one audio may be sent to: an absolute https URL without
    /// embedded credentials, as the integrations require of internet providers, or plain http to this machine only.
    /// </summary>
    public static Uri? TryCreateEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return null;
        }

        return parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback) ? parsed : null;
    }
}

/// <summary>Per-user quotas (<c>ai.limits</c>), on top of the 20-requests-per-minute burst limiter.</summary>
internal sealed record AiLimits
{
    public int PerUserPerHour { get; init; } = 60;

    public int PerUserPerDay { get; init; } = 300;
}

/// <summary>
/// Reads AI settings for the signed-in user's facility. Malformed values are treated as "not configured" (logged by
/// key, never by value) so a typo disables AI instead of breaking the request.
/// </summary>
internal sealed partial class AiSettingsReader(ISettingsService settings, ICurrentUser currentUser, ILogger<AiSettingsReader> logger)
{
    public async Task<AiTextSettings> GetTextSettingsAsync(CancellationToken cancellationToken)
    {
        var enabled = await TryGetAsync<bool?>(AiSettingKeys.Enabled, cancellationToken) ?? false;
        var provider = (await TryGetAsync<string>(AiSettingKeys.Provider, cancellationToken))?.Trim().ToLowerInvariant() ?? AiProviders.Anthropic;
        var model = await TryGetAsync<string>(AiSettingKeys.Model, cancellationToken);
        var timeout = await TryGetAsync<int?>(AiSettingKeys.TimeoutSeconds, cancellationToken) ?? 90;

        // The key is decrypted only when it would actually be used.
        var apiKey = enabled && provider == AiProviders.Anthropic ? await ReadSecretAsync(AiSettingKeys.AnthropicSecret, "apiKey", cancellationToken) : null;
        var validModel = model is not null && ModelPattern().IsMatch(model.Trim()) ? model.Trim() : ClaudeDefaults.Model;
        return new AiTextSettings(enabled, provider, validModel, apiKey, TimeSpan.FromSeconds(Math.Clamp(timeout, 10, 300)));
    }

    public async Task<SpeechConfig> GetSpeechConfigAsync(CancellationToken cancellationToken)
    {
        var speech = await TryGetAsync<SpeechSettings>(AiSettingKeys.Speech, cancellationToken) ?? new SpeechSettings();
        var enabled = await TryGetAsync<bool?>(AiSettingKeys.Enabled, cancellationToken) ?? false;
        var endpoint = SpeechConfig.TryCreateEndpoint(speech.Url);

        // The key is decrypted only when it would actually be used.
        var apiKey = enabled && endpoint is not null && string.Equals(speech.Provider, AiProviders.Whisper, StringComparison.OrdinalIgnoreCase)
            ? await ReadSecretAsync(AiSettingKeys.SpeechSecret, "apiKey", cancellationToken)
            : null;
        return new SpeechConfig(speech, endpoint, apiKey, enabled);
    }

    public async Task<AiLimits> GetLimitsAsync(CancellationToken cancellationToken) =>
        await TryGetAsync<AiLimits>(AiSettingKeys.Limits, cancellationToken) ?? new AiLimits();

    private async Task<T?> TryGetAsync<T>(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetAsync<T>(key, currentUser.FacilityId, cancellationToken);
        }
        catch (JsonException)
        {
            logger.LogWarning("Setting {Key} has an invalid value; using the default", key);
            return default;
        }
    }

    private async Task<string?> ReadSecretAsync(string key, string property, CancellationToken cancellationToken)
    {
        try
        {
            var value = await settings.GetAsync<JsonElement>(key, currentUser.FacilityId, cancellationToken);
            var secret = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object when value.TryGetProperty(property, out var inner) && inner.ValueKind == JsonValueKind.String => inner.GetString(),
                _ => null,
            };
            return string.IsNullOrWhiteSpace(secret) ? null : secret.Trim();
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            logger.LogWarning("Secret setting {Key} could not be read ({ErrorType})", key, ex.GetType().Name);
            return null;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,63}$")]
    private static partial Regex ModelPattern();
}
