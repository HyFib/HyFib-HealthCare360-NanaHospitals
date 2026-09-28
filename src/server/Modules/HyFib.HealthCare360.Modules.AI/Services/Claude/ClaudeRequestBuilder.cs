using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services.Claude;

internal static class ClaudeDefaults
{
    /// <summary>Default model: the latest, most capable Claude (override per facility with the <c>ai.model</c> setting).</summary>
    public const string Model = "claude-opus-5-5";

    /// <summary>
    /// Server-side refusal fallback. Claude's safety classifiers occasionally decline benign life-sciences text; with
    /// <c>fallbacks: "default"</c> the API re-runs such a request on Anthropic's recommended fallback model instead of
    /// returning a refusal to the clinician.
    /// </summary>
    public const string FallbackBeta = "server-side-fallback-2026-07-01";
}

/// <summary>
/// Builds the Messages API request for a platform <see cref="AiRequest"/>: structured output when the caller supplies a
/// JSON schema, adaptive thinking (the model default) with an effort chosen per purpose — low for latency-sensitive
/// voice commands, high for clinical note structuring where accuracy matters most.
/// </summary>
internal static class ClaudeRequestBuilder
{
    public static MessageCreateParams Build(AiRequest request, string model)
    {
        var messages = request.Messages
            .Select(m => new BetaMessageParam
            {
                Role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? Role.Assistant : Role.User,
                Content = m.Content,
            })
            .ToList();

        var output = string.IsNullOrWhiteSpace(request.JsonSchema)
            ? new BetaOutputConfig { Effort = EffortFor(request.Purpose) }
            : new BetaOutputConfig { Effort = EffortFor(request.Purpose), Format = new BetaJsonOutputFormat { Schema = ParseSchema(request.JsonSchema) } };

        return new MessageCreateParams
        {
            Model = model,
            // Thinking is on by default and max_tokens covers thinking plus the answer, so never size it tightly.
            MaxTokens = Math.Clamp(request.MaxTokens, 1024, 32_000),
            System = request.SystemPrompt,
            Messages = messages,
            OutputConfig = output,
            Fallbacks = new Default(),
            Betas = [ClaudeDefaults.FallbackBeta],
        };
    }

    internal static Effort EffortFor(string purpose) =>
        purpose.StartsWith("assistant.", StringComparison.Ordinal) ? Effort.Low : Effort.High;

    /// <exception cref="JsonException">The schema is not a JSON object.</exception>
    internal static Dictionary<string, JsonElement> ParseSchema(string schema) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema) ?? throw new JsonException("The JSON schema is empty.");
}
