using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Timeout;

namespace HyFib.HealthCare360.Modules.AI.Services.OpenAi;

/// <summary>
/// The <c>openai</c> text provider: an OpenAI-compatible chat-completions server, <c>POST {baseUrl}/chat/completions</c>
/// with a bearer key when one is set. The same format serves Azure OpenAI and model servers inside the hospital (vLLM,
/// llama.cpp server, Ollama), so a hospital can keep text on its own network. A typed <see cref="HttpClient"/> behind the
/// standard resilience pipeline; the POST is never retried, because the server may already be working on it. Requests
/// arrive already redacted (SEC-12). Failures become a failed <see cref="AiCompletion"/> with a staff-facing message;
/// logs carry the status code or error category only, never text.
/// </summary>
internal sealed class OpenAiCompatibleTextService(HttpClient http, ILogger<OpenAiCompatibleTextService> logger) : IAiTextProvider
{
    public string Key => AiProviders.OpenAi;

    public Task<AiTextSettings> ReadSettingsAsync(AiSettingsReader settings, bool enabled, CancellationToken cancellationToken) =>
        settings.GetOpenAiSettingsAsync(enabled, cancellationToken);

    public async Task<AiCompletion> CompleteAsync(RedactedAiRequest request, AiTextSettings settings, CancellationToken cancellationToken)
    {
        var purpose = request.Request.Purpose;
        if (settings.Endpoint is not { } endpoint)
        {
            return Failed(settings.Model, "No AI server address is configured.");
        }

        JsonObject body;
        try
        {
            body = OpenAiRequestBuilder.Build(request.Request, settings.Model);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid JSON schema for AI purpose {Purpose}", purpose);
            return Failed(settings.Model, "The AI request could not be prepared.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        // ai.openai.timeoutSeconds bounds the whole call, so a screen that waits a little longer (the status endpoint
        // tells it how long) always gets the answer instead of giving up while this still runs.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(settings.Timeout);
        try
        {
            using var response = await http.SendAsync(message, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The AI server answered HTTP {Status} for purpose {Purpose}", (int)response.StatusCode, purpose);
                return Failed(settings.Model, Describe(response.StatusCode, settings.Model));
            }

            return OpenAiResponseMapper.Map(await response.Content.ReadAsStringAsync(deadline.Token), settings.Model);
        }
        catch (Exception ex) when (ex is TimeoutRejectedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("The AI server call for purpose {Purpose} timed out", purpose);
            return Failed(settings.Model, "The AI service did not respond in time.");
        }
        catch (ExecutionRejectedException ex)
        {
            // The circuit is open after repeated failures, or too many calls are already waiting.
            logger.LogWarning("The AI server call for purpose {Purpose} was not made: {Type}", purpose, ex.GetType().Name);
            return Failed(settings.Model, "The AI service is temporarily unavailable.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Could not reach the AI server for purpose {Purpose}: {Error}", purpose, ex.HttpRequestError);
            return Failed(settings.Model, "The AI service could not be reached.");
        }
    }

    private static string Describe(HttpStatusCode status, string model) => status switch
    {
        HttpStatusCode.Unauthorized => "The AI service rejected the configured API key.",
        HttpStatusCode.NotFound => $"The AI server does not offer the model '{model}' at this address.",
        HttpStatusCode.TooManyRequests => "The AI service is busy. Please try again in a minute.",
        >= HttpStatusCode.InternalServerError => "The AI service is temporarily unavailable.",
        _ => "The AI service could not process this request.",
    };

    private static AiCompletion Failed(string model, string error) => new(false, null, model, 0, 0, error);
}
