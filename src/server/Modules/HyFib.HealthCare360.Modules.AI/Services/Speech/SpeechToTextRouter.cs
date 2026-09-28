using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services.Speech;

/// <summary>
/// This server's <see cref="ISpeechToText"/>: <c>none</c> by default (HC360 Desktop dictates with offline Windows speech)
/// or a Whisper-compatible endpoint configured in <c>ai.speech</c>.
/// </summary>
internal sealed class SpeechToTextRouter(AiSettingsReader settings, WhisperSpeechToText whisper) : ISpeechToText
{
    private SpeechConfig? _config;

    /// <remarks>Synchronous by contract; blocks only on the first (memory-cached) settings read in a scope.</remarks>
    public bool IsEnabled => (_config ??= settings.GetSpeechConfigAsync(CancellationToken.None).GetAwaiter().GetResult()).IsUsable;

    public async Task<SpeechConfig> GetConfigAsync(CancellationToken cancellationToken) =>
        _config ??= await settings.GetSpeechConfigAsync(cancellationToken);

    /// <exception cref="SpeechToTextException">Not configured, unreachable or an unusable answer.</exception>
    public async Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken cancellationToken)
    {
        var config = await GetConfigAsync(cancellationToken);
        if (!config.IsUsable)
        {
            throw new SpeechToTextException(config.Reason ?? "Server speech-to-text is not configured.");
        }

        var mediaType = AudioFormat.Normalize(contentType) ?? throw new SpeechToTextException("Unsupported audio format.");
        var lang = string.IsNullOrWhiteSpace(language) ? config.Settings.Language : language.Trim().ToLowerInvariant();
        return await whisper.TranscribeAsync(audio, mediaType, lang, config, cancellationToken);
    }
}
