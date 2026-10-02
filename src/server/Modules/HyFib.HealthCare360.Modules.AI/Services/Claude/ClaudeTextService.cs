using System.Text.Json;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.AI.Services.Claude;

/// <summary>
/// Anthropic Claude through the official C# SDK. Prompts arrive already redacted (SEC-12). Failures become a failed
/// <see cref="AiCompletion"/> with a staff-facing message; logs carry the error category only, never prompt text.
/// </summary>
internal sealed class ClaudeTextService(IAnthropicClientProvider clients, ILogger<ClaudeTextService> logger)
{
    public async Task<AiCompletion> CompleteAsync(AiRequest request, AiTextSettings settings, CancellationToken cancellationToken)
    {
        MessageCreateParams parameters;
        try
        {
            parameters = ClaudeRequestBuilder.Build(request, settings.Model);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Invalid JSON schema for AI purpose {Purpose}", request.Purpose);
            return Failed(settings.Model, "The AI request could not be prepared.");
        }

        var client = clients.GetClient(settings.ApiKey!, settings.Timeout);

        // ai.timeoutSeconds bounds the whole call, the SDK's retries included, so a screen that waits a little longer
        // (the status endpoint tells it how long) always gets the answer instead of giving up while this still runs.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(settings.Timeout);
        try
        {
            var response = await client.Beta.Messages.Create(parameters, deadline.Token);
            return ClaudeResponseMapper.Map(response);
        }
        catch (AnthropicUnauthorizedException)
        {
            logger.LogWarning("Anthropic rejected the configured API key");
            return Failed(settings.Model, "The AI service rejected the configured API key.");
        }
        catch (AnthropicNotFoundException)
        {
            logger.LogWarning("Model {Model} is not available to this Anthropic account", settings.Model);
            return Failed(settings.Model, $"The AI model '{settings.Model}' is not available to this account.");
        }
        catch (AnthropicRateLimitException)
        {
            logger.LogWarning("Anthropic rate limit reached for purpose {Purpose}", request.Purpose);
            return Failed(settings.Model, "The AI service is busy. Please try again in a minute.");
        }
        catch (Anthropic5xxException ex)
        {
            logger.LogWarning("Anthropic service error for purpose {Purpose}: {Type}", request.Purpose, ex.GetType().Name);
            return Failed(settings.Model, "The AI service is temporarily unavailable.");
        }
        catch (AnthropicIOException ex)
        {
            logger.LogWarning("Could not reach Anthropic for purpose {Purpose}: {Type}", request.Purpose, ex.GetType().Name);
            return Failed(settings.Model, "The AI service could not be reached. Check the internet connection.");
        }
        catch (AnthropicApiException ex)
        {
            logger.LogWarning("Anthropic API error for purpose {Purpose}: {Type}", request.Purpose, ex.GetType().Name);
            return Failed(settings.Model, "The AI service could not process this request.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Anthropic call for purpose {Purpose} timed out", request.Purpose);
            return Failed(settings.Model, "The AI service did not respond in time.");
        }
        catch (AnthropicException ex)
        {
            logger.LogWarning("Anthropic SDK error for purpose {Purpose}: {Type}", request.Purpose, ex.GetType().Name);
            return Failed(settings.Model, "The AI service could not process this request.");
        }
    }

    private static AiCompletion Failed(string model, string error) => new(false, null, model, 0, 0, error);
}
