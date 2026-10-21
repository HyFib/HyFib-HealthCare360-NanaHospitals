using System.Text.Json;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services.OpenAi;

/// <summary>
/// Maps a chat-completions answer to the platform's <see cref="AiCompletion"/>, as the Claude mapper does: the finish
/// reason is checked before the content is read, so a refusal, a filtered answer or a truncated one is reported as a
/// failure, never returned as if it were a complete draft. An answer that is not a chat completion with text is a
/// failure too. Reasoning a server returns apart from the answer (<c>reasoning_content</c>) is not used. The model is
/// reported as configured, the name the hospital knows (some servers answer with another name).
/// </summary>
internal static class OpenAiResponseMapper
{
    public static AiCompletion Map(string json, string model)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Unexpected(model, 0, 0);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unexpected(model, 0, 0);
            }

            var input = Tokens(root, "prompt_tokens");
            var output = Tokens(root, "completion_tokens");
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0
                || choices[0] is not { ValueKind: JsonValueKind.Object } choice
                || !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            {
                return Unexpected(model, input, output);
            }

            var finishReason = Text(choice, "finish_reason");
            if (Text(message, "refusal") is { Length: > 0 } || finishReason == "content_filter")
            {
                return new AiCompletion(false, null, model, input, output, "The AI service declined this request. Please write the note manually.");
            }

            if (finishReason == "length")
            {
                return new AiCompletion(false, null, model, input, output, "The AI response was cut off before it was complete.");
            }

            return Text(message, "content") is { } content && !string.IsNullOrWhiteSpace(content)
                ? new AiCompletion(true, content, model, input, output, null)
                : new AiCompletion(false, null, model, input, output, "The AI service returned an empty answer.");
        }
    }

    private static AiCompletion Unexpected(string model, int input, int output) =>
        new(false, null, model, input, output, "The AI service returned an answer HC360 could not read.");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Tokens(JsonElement root, string name) =>
        root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
        && usage.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var value)
            ? (int)Math.Clamp(value, 0, int.MaxValue)
            : 0;
}
