using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class ClaudeResponseMapperTests
{
    /// <summary>A Messages API response as the API returns it; <paramref name="content"/> is the JSON content array.</summary>
    internal static BetaMessage Message(string stopReason, string content) =>
        BetaMessage.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($$"""
            {
              "id": "msg_01XFDUDYJgAACzvnptvVoYEL",
              "type": "message",
              "role": "assistant",
              "model": "claude-opus-5",
              "content": {{content}},
              "stop_reason": "{{stopReason}}",
              "stop_sequence": null,
              "usage": { "input_tokens": 812, "output_tokens": 64 }
            }
            """)!);

    [Fact]
    public void Joins_the_text_blocks_and_skips_thinking()
    {
        var completion = ClaudeResponseMapper.Map(Message("end_turn", """
            [
              { "type": "thinking", "thinking": "The user wants the billing screen.", "signature": "EqQBCkYIBRgCKkB" },
              { "type": "text", "text": "{\"intent\":" },
              { "type": "text", "text": "\"navigate\"}" }
            ]
            """));

        completion.ShouldBe(new AiCompletion(true, """{"intent":"navigate"}""", "claude-opus-5", 812, 64, null));
    }

    [Fact]
    public void A_refusal_is_a_failure_even_when_text_was_returned() =>
        ClaudeResponseMapper.Map(Message("refusal", """[{ "type": "text", "text": "I can't help with that." }]"""))
            .ShouldBe(new AiCompletion(false, null, "claude-opus-5", 812, 64, "The AI service declined this request. Please write the note manually."));

    [Fact]
    public void A_truncated_answer_is_never_returned_as_complete()
    {
        var completion = ClaudeResponseMapper.Map(Message("max_tokens", """[{ "type": "text", "text": "{\"subjective\":\"Fever" }]"""));

        completion.Success.ShouldBeFalse();
        completion.Text.ShouldBeNull();
        completion.Error.ShouldBe("The AI response was cut off before it was complete.");
    }

    [Fact]
    public void An_empty_answer_is_a_failure() =>
        ClaudeResponseMapper.Map(Message("end_turn", "[]")).Error.ShouldBe("The AI service returned an empty answer.");
}
