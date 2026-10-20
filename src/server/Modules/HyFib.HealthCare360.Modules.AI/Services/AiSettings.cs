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

    /// <summary>
    /// The text provider: <c>anthropic</c> (the default when unset), <c>openai</c> (an OpenAI-compatible server), or
    /// anything else (<c>disabled</c>) to turn AI text off. A value no provider has never falls back to another one.
    /// </summary>
    public const string Provider = "ai.provider";

    /// <summary>The Anthropic provider's model; the key predates other providers, so it has no provider in its name.</summary>
    public const string Model = "ai.model";

    /// <summary>The Anthropic provider's time limit in seconds (10–300, default 90).</summary>
    public const string TimeoutSeconds = "ai.timeoutSeconds";

    /// <summary>The Anthropic API key: a JSON string, or <c>{"apiKey":"…"}</c>.</summary>
    public const string AnthropicSecret = "ai.anthropic.secret";

    /// <summary>The OpenAI-compatible server: <see cref="OpenAiSettings"/>.</summary>
    public const string OpenAi = "ai.openai";

    /// <summary>The OpenAI-compatible server's bearer key, when it needs one: a JSON string, or <c>{"apiKey":"…"}</c>.</summary>
    public const string OpenAiSecret = "ai.openai.secret";

    public const string Speech = "ai.speech";
    public const string SpeechSecret = "ai.speech.secret";
    public const string Limits = "ai.limits";
}

internal static class AiProviders
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";
    public const string None = "none";
    public const string Whisper = "whisper";
}

/// <summary>
/// Resolved AI text settings for the current facility: the switch, the chosen provider and that provider's own settings.
/// <see cref="Problem"/> is the provider's reason it cannot be used (no key, no address), looked for only while AI is
/// on. <see cref="Endpoint"/> is the address requests are sent to, for providers configured with one.
/// </summary>
internal sealed record AiTextSettings(
    bool Enabled, string Provider, string Model, string? ApiKey, TimeSpan Timeout, Uri? Endpoint = null, string? Problem = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    public bool IsUsable => Reason is null;

    public string? Reason => !Enabled ? "AI features are turned off for this facility (setting ai.enabled)." : Problem;

    /// <summary>
    /// <c>ai.provider</c> names a provider this server does not have (or <c>disabled</c>): nothing is sent, and no other
    /// provider is used instead, since that would send text to a service the hospital did not choose.
    /// </summary>
    public static AiTextSettings NotAvailable(bool enabled, string provider) =>
        new(enabled, provider, string.Empty, null, DefaultTimeout, Problem: $"AI provider '{provider}' is not available.");
}

/// <summary>
/// An OpenAI-compatible chat-completions server (<c>ai.openai</c>, chosen with <c>ai.provider</c> = <c>openai</c>): Azure
/// OpenAI, or a model server inside the hospital (vLLM, llama.cpp server, Ollama's OpenAI endpoint), so that text need
/// not leave the hospital at all. Its key, when it needs one, is the secret setting <c>ai.openai.secret</c>.
/// </summary>
internal sealed record OpenAiSettings
{
    /// <summary>
    /// The address that <c>/chat/completions</c> is added to, usually ending in <c>/v1</c>: <c>https://llm.hospital.lan/v1</c>,
    /// <c>http://localhost:8000/v1</c>, or for Azure OpenAI <c>https://{resource}.openai.azure.com/openai/v1</c>. An address
    /// that already ends in <c>/chat/completions</c> is used as it is. See <see cref="AiEndpoint"/> for the rule.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>The model as the server names it (its <c>/v1/models</c> list); for Azure OpenAI, the deployment name.</summary>
    public string? Model { get; init; }

    public int TimeoutSeconds { get; init; } = 90;
}

/// <summary>
/// Where HC360 may send AI text or dictation audio (SEC-12): an absolute https URL without a user name or password, as
/// the integrations require of internet providers (<c>IntegrationHttp.TryCreateEndpoint</c>), or plain http to this
/// computer only (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>), where nothing crosses the network.
/// <para>
/// Plain http to another computer on the hospital network is refused on purpose, unlike for Tally or PACS. Dictation and
/// notes are clinical content even with the identifiers removed, a bearer key would travel in clear text, and someone
/// else on the WiFi is a threat HC360 is designed against (SECURITY.md §1). A model server elsewhere on the network gets
/// a TLS certificate the Server Node trusts, for example behind a reverse proxy.
/// </para>
/// </summary>
internal static class AiEndpoint
{
    public static Uri? TryCreate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return null;
        }

        return parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback) ? parsed : null;
    }

    /// <summary>
    /// The chat-completions URL for an OpenAI-compatible <paramref name="baseUrl"/>, under the same rule. A query or
    /// fragment is refused: the request needs neither, and a key must not ride in the address.
    /// </summary>
    public static Uri? ChatCompletions(string? baseUrl)
    {
        if (TryCreate(baseUrl) is not { Query: "", Fragment: "" } endpoint)
        {
            return null;
        }

        var address = endpoint.AbsoluteUri.TrimEnd('/');
        return address.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? new Uri(address) : new Uri(address + "/chat/completions");
    }
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
    /// for a server on this machine (<c>localhost</c>), where the audio never crosses the network (<see cref="AiEndpoint"/>).
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
    /// <summary>
    /// The switch (<c>ai.enabled</c>, off unless true) and the chosen text provider (<c>ai.provider</c>, lower case),
    /// which is Anthropic when unset, as it was before the provider could be chosen, so existing settings keep working.
    /// </summary>
    public async Task<(bool Enabled, string Provider)> GetTextChoiceAsync(CancellationToken cancellationToken)
    {
        var enabled = await TryGetAsync<bool?>(AiSettingKeys.Enabled, cancellationToken) ?? false;
        var provider = await TryGetAsync<string>(AiSettingKeys.Provider, cancellationToken);
        return (enabled, string.IsNullOrWhiteSpace(provider) ? AiProviders.Anthropic : provider.Trim().ToLowerInvariant());
    }

    /// <summary>Anthropic: <c>ai.model</c>, <c>ai.timeoutSeconds</c> and the key in <c>ai.anthropic.secret</c>.</summary>
    public async Task<AiTextSettings> GetAnthropicSettingsAsync(bool enabled, CancellationToken cancellationToken)
    {
        var model = await TryGetAsync<string>(AiSettingKeys.Model, cancellationToken);
        var timeout = await TryGetAsync<int?>(AiSettingKeys.TimeoutSeconds, cancellationToken) ?? 90;

        // The key is decrypted only when it would actually be used.
        var apiKey = enabled ? await ReadSecretAsync(AiSettingKeys.AnthropicSecret, "apiKey", cancellationToken) : null;
        var validModel = model is not null && ModelPattern().IsMatch(model.Trim()) ? model.Trim() : ClaudeDefaults.Model;
        return new AiTextSettings(enabled, AiProviders.Anthropic, validModel, apiKey, TimeSpan.FromSeconds(Math.Clamp(timeout, 10, 300)),
            Problem: enabled && apiKey is null ? "No Anthropic API key is configured (secret setting ai.anthropic.secret)." : null);
    }

    /// <summary>
    /// The OpenAI-compatible server: <c>ai.openai</c> (address, model, time limit) and the optional key in
    /// <c>ai.openai.secret</c>. There is no default model: the hospital names the one its server runs.
    /// </summary>
    public async Task<AiTextSettings> GetOpenAiSettingsAsync(bool enabled, CancellationToken cancellationToken)
    {
        var server = await TryGetAsync<OpenAiSettings>(AiSettingKeys.OpenAi, cancellationToken) ?? new OpenAiSettings();
        var endpoint = AiEndpoint.ChatCompletions(server.BaseUrl);
        var model = server.Model?.Trim() is { } name && OpenAiModelPattern().IsMatch(name) ? name : null;
        var problem = endpoint is null
            ? "The AI server address (setting ai.openai.baseUrl) must be an https address without a user name, password or query (plain http only to localhost)."
            : model is null ? "No model is set for the AI server (setting ai.openai.model)."
            : null;

        // The key is decrypted only when it would actually be used. A server inside the hospital may need none.
        var apiKey = enabled && problem is null ? await ReadSecretAsync(AiSettingKeys.OpenAiSecret, "apiKey", cancellationToken) : null;
        return new AiTextSettings(enabled, AiProviders.OpenAi, model ?? string.Empty, apiKey, TimeSpan.FromSeconds(Math.Clamp(server.TimeoutSeconds, 10, 300)),
            endpoint, enabled ? problem : null);
    }

    public async Task<SpeechConfig> GetSpeechConfigAsync(CancellationToken cancellationToken)
    {
        var speech = await TryGetAsync<SpeechSettings>(AiSettingKeys.Speech, cancellationToken) ?? new SpeechSettings();
        var enabled = await TryGetAsync<bool?>(AiSettingKeys.Enabled, cancellationToken) ?? false;
        var endpoint = AiEndpoint.TryCreate(speech.Url);

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

    /// <summary>Model names on OpenAI-compatible servers also use capitals, <c>_</c>, <c>:</c> (a tag) and <c>/</c> (an organisation).</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$")]
    private static partial Regex OpenAiModelPattern();
}
