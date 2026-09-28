using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class ClaudeRequestBuilderTests
{
    private static readonly AiRequest Command = AiPrompts.Command("go to [UHID_1]", "/emr", patientOpen: true, TestClock.Today);

    [Fact]
    public void Sends_the_system_prompt_and_messages_to_the_configured_model()
    {
        var parameters = ClaudeRequestBuilder.Build(Command, "claude-opus-5");

        parameters.Model.Raw().ShouldBe("claude-opus-5");
        parameters.System.ShouldNotBeNull().TryPickString(out var system).ShouldBeTrue();
        system.ShouldBe(AiPrompts.CommandSystem);
        var message = parameters.Messages.ShouldHaveSingleItem();
        message.Role.Raw().ShouldBe("user");
        message.Content.TryPickString(out var content).ShouldBeTrue();
        content!.ShouldContain("Command: go to [UHID_1]");
    }

    [Fact]
    public void Constrains_the_answer_with_the_callers_json_schema()
    {
        var parameters = ClaudeRequestBuilder.Build(Command, "claude-opus-5");

        var schema = parameters.OutputConfig.ShouldNotBeNull().Format.ShouldNotBeNull().Schema;
        schema["type"].GetString().ShouldBe("object");
        schema["additionalProperties"].GetBoolean().ShouldBeFalse();
        schema["required"].EnumerateArray().Select(e => e.GetString()).ShouldContain("intent");
    }

    [Fact]
    public void Voice_commands_use_low_effort_and_clinical_notes_high_effort()
    {
        EffortOf(ClaudeRequestBuilder.Build(Command, "claude-opus-5")).ShouldBe("low");
        EffortOf(ClaudeRequestBuilder.Build(AiPrompts.StructureNote("fever", null), "claude-opus-5")).ShouldBe("high");
        ClaudeRequestBuilder.EffortFor("assistant.command").ShouldBe(Effort.Low);
        ClaudeRequestBuilder.EffortFor("clinical.structure-note").ShouldBe(Effort.High);
    }

    private static string? EffortOf(MessageCreateParams parameters) => parameters.OutputConfig?.Effort is { } effort ? effort.Raw() : null;

    [Theory]
    [InlineData(100, 1024L)]
    [InlineData(2048, 2048L)]
    [InlineData(16_000, 16_000L)]
    [InlineData(64_000, 32_000L)]
    public void Leaves_room_for_thinking_in_max_tokens(int requested, long expected) =>
        ClaudeRequestBuilder.Build(Command with { MaxTokens = requested }, "claude-opus-5").MaxTokens.ShouldBe(expected);

    [Fact]
    public void Opts_into_the_server_side_refusal_fallback()
    {
        var parameters = ClaudeRequestBuilder.Build(Command, "claude-opus-5");

        parameters.Fallbacks.ShouldNotBeNull();
        parameters.Betas.ShouldNotBeNull().Select(b => b.Raw()).ShouldBe([ClaudeDefaults.FallbackBeta]);
    }

    [Fact]
    public void Requests_without_a_schema_ask_for_plain_text()
    {
        var parameters = ClaudeRequestBuilder.Build(new AiRequest("assistant.chat", "Be brief.", [new AiMessage("user", "hello")]), "claude-opus-5");

        parameters.OutputConfig.ShouldNotBeNull().Format.ShouldBeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    public void Refuses_a_schema_that_is_not_a_json_object(string schema) =>
        Should.Throw<JsonException>(() => ClaudeRequestBuilder.ParseSchema(schema));
}
