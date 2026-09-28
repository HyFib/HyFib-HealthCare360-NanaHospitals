using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.AI.Services.Speech;

/// <summary>Speech-to-text failure with a staff-facing message.</summary>
internal sealed class SpeechToTextException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// OpenAI-compatible transcription endpoint (<c>POST …/audio/transcriptions</c>, multipart <c>file</c>/<c>model</c>/
/// <c>language</c>) — typically an on-prem faster-whisper server, so dictation audio never leaves the hospital.
/// Only sizes and status codes are logged, never audio or text.
/// </summary>
internal sealed class WhisperSpeechToText(HttpClient http, ILogger<WhisperSpeechToText> logger)
{
    public async Task<string> TranscribeAsync(Stream audio, string mediaType, string language, SpeechConfig config, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", "dictation" + AudioFormat.Extension(mediaType));
        form.Add(new StringContent(config.Settings.Model), "model");
        form.Add(new StringContent(language), "language");
        form.Add(new StringContent("json"), "response_format");

        using var request = new HttpRequestMessage(HttpMethod.Post, config.Endpoint) { Content = form };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.Settings.TimeoutSeconds, 10, 170)));
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Speech server returned HTTP {Status}", (int)response.StatusCode);
                throw new SpeechToTextException($"The speech server returned HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return document.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()?.Trim() ?? string.Empty
                : throw new SpeechToTextException("The speech server returned an unexpected response.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpeechToTextException("The speech server did not respond in time.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or Polly.Timeout.TimeoutRejectedException or Polly.CircuitBreaker.BrokenCircuitException)
        {
            logger.LogWarning("Speech server call failed: {Type}", ex.GetType().Name);
            throw new SpeechToTextException("The speech server could not be reached.", ex);
        }
    }
}
