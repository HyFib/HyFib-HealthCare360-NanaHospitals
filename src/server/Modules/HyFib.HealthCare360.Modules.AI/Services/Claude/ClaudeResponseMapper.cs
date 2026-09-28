using System.Text;
using Anthropic.Models.Beta.Messages;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services.Claude;

/// <summary>
/// Maps a Messages API response to the platform's <see cref="AiCompletion"/>. The stop reason is checked before the
/// content is read: a refusal (after any fallback) or a truncated answer is reported as a failure, never returned as
/// if it were a complete draft.
/// </summary>
internal static class ClaudeResponseMapper
{
    public static AiCompletion Map(BetaMessage response)
    {
        var model = response.Model.Raw();
        var input = ToInt(response.Usage.InputTokens);
        var output = ToInt(response.Usage.OutputTokens);
        var stopReason = response.StopReason?.Raw();

        if (stopReason == "refusal")
        {
            return new AiCompletion(false, null, model, input, output, "The AI service declined this request. Please write the note manually.");
        }

        if (stopReason == "max_tokens")
        {
            return new AiCompletion(false, null, model, input, output, "The AI response was cut off before it was complete.");
        }

        var text = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var textBlock))
            {
                text.Append(textBlock.Text);
            }
        }

        return text.Length == 0
            ? new AiCompletion(false, null, model, input, output, "The AI service returned an empty answer.")
            : new AiCompletion(true, text.ToString(), model, input, output, null);
    }

    private static int ToInt(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
}
