using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services.OpenAi;

/// <summary>
/// Builds the chat-completions body for a platform <see cref="AiRequest"/>, in the part of the format that OpenAI, Azure
/// OpenAI, vLLM, llama.cpp server and Ollama all accept:
/// <list type="bullet">
/// <item>the model, the system prompt and the messages, and <c>stream: false</c>;</item>
/// <item>the answer budget as <c>max_completion_tokens</c>; a server that does not know the field ignores it, and the time
/// limit still bounds the call;</item>
/// <item>with a JSON schema, <c>response_format</c> of type <c>json_schema</c>, so the answer is structured and parsing
/// never depends on free text.</item>
/// </list>
/// No sampling settings (temperature and the like) are sent: models that reason refuse some of them, so they are left to
/// the server's own configuration.
/// </summary>
internal static partial class OpenAiRequestBuilder
{
    public static JsonObject Build(AiRequest request, string model)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        }

        foreach (var message in request.Messages)
        {
            messages.Add(new JsonObject
            {
                ["role"] = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                ["content"] = message.Content,
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_completion_tokens"] = Math.Clamp(request.MaxTokens, 1, 32_000),
            ["stream"] = false,
        };

        if (!string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = SchemaName(request.Purpose),
                    ["strict"] = true,
                    ["schema"] = ParseSchema(request.JsonSchema),
                },
            };
        }

        return body;
    }

    /// <summary>The schema's name as OpenAI requires it, 1–64 letters, digits, <c>_</c> or <c>-</c>: <c>clinical_structure-note</c>.</summary>
    internal static string SchemaName(string purpose)
    {
        var name = NotAllowedInName().Replace(purpose, "_");
        return name.Length == 0 ? "answer" : name.Length > 64 ? name[..64] : name;
    }

    /// <exception cref="JsonException">The schema is not a JSON object.</exception>
    internal static JsonObject ParseSchema(string schema) =>
        JsonNode.Parse(schema) as JsonObject ?? throw new JsonException("The JSON schema is not an object.");

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex NotAllowedInName();
}
