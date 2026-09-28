using HyFib.HealthCare360.Modules.AI.Features.Assistant;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Assistant;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Integrations;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class AssistantCommandServiceTests
{
    private const string Unrecognised = "who was the last patient I saw, the one with 9876543210";

    private static readonly Guid FoundPatient = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");

    private readonly IAiGateway _ai = Substitute.For<IAiGateway>();
    private readonly IAiQuota _quota = Substitute.For<IAiQuota>();
    private readonly IAiUsageRecorder _usage = Substitute.For<IAiUsageRecorder>();
    private readonly IPatientDirectory _patients = Substitute.For<IPatientDirectory>();

    public AssistantCommandServiceTests()
    {
        _ai.GetAvailabilityAsync(Ct).ReturnsForAnyArgs(new AiAvailability(true, "anthropic", "claude-opus-5", null));
        // NSubstitute would otherwise answer "" (an auto value), which reads as "over quota".
        _quota.CheckAsync(Ct).ReturnsForAnyArgs((string?)null);
        _patients.RedactionContextAsync(null, Ct).ReturnsForAnyArgs(RedactionContext.Empty);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AssistantCommandService Service => new(_ai, _quota, _usage, _patients, new TestClock());

    private Task<AssistantCommandResponse> Execute(string text, Guid? patientId = null) =>
        Service.ExecuteAsync(new AssistantCommandRequest(text, new AssistantContext("/emr", patientId)), Ct);

    private void AiAnswers(AiCompletion completion) => _ai.CompleteAsync(null!, Ct).ReturnsForAnyArgs(completion);

    [Fact]
    public async Task Understood_commands_never_reach_the_ai_provider()
    {
        var response = await Execute("go to billing");

        response.Intent.ShouldBe(AssistantIntents.Navigate);
        response.Source.ShouldBe("rules");
        response.Parameters["route"].ShouldBe("/billing");
        response.ConfirmationText.ShouldBe("Opening Billing.");
        await _ai.DidNotReceiveWithAnyArgs().GetAvailabilityAsync(Ct);
        await _ai.DidNotReceiveWithAnyArgs().CompleteAsync(null!, Ct);
    }

    [Fact]
    public async Task Resolves_a_spoken_uhid_to_the_patient_record()
    {
        _patients.FindByUhidAsync("NH26000123", Ct).ReturnsForAnyArgs(FoundPatient);

        var response = await Execute("open patient UHID NH26000123");

        response.Intent.ShouldBe(AssistantIntents.OpenPatient);
        response.Parameters["patientId"].ShouldBe(FoundPatient.ToString());
        response.Parameters["patientFound"].ShouldBe("true");
        response.ConfirmationText.ShouldBe("Opening patient NH26000123.");
    }

    [Fact]
    public async Task Says_so_when_no_patient_has_the_uhid()
    {
        _patients.FindByUhidAsync("NH26000999", Ct).ReturnsForAnyArgs((Guid?)null);

        var response = await Execute("open patient UHID NH26000999");

        response.Parameters["patientFound"].ShouldBe("false");
        response.Parameters.ContainsKey("patientId").ShouldBeFalse();
        response.ConfirmationText.ShouldBe("No patient was found with UHID NH26000999.");
    }

    [Fact]
    public async Task Stays_offline_when_ai_is_turned_off()
    {
        _ai.GetAvailabilityAsync(Ct).ReturnsForAnyArgs(new AiAvailability(false, "anthropic", null, "AI features are turned off."));

        var response = await Execute(Unrecognised);

        response.Intent.ShouldBe(AssistantIntents.Unknown);
        response.Source.ShouldBe("rules");
        await _ai.DidNotReceiveWithAnyArgs().CompleteAsync(null!, Ct);
        await _usage.DidNotReceiveWithAnyArgs().RecordAsync(null!, Ct);
    }

    [Fact]
    public async Task Explains_when_the_user_is_over_quota()
    {
        _quota.CheckAsync(Ct).ReturnsForAnyArgs("You have reached the limit of 60 AI requests per hour. Please try again later.");

        var response = await Execute(Unrecognised);

        response.ConfirmationText.ShouldBe("You have reached the limit of 60 AI requests per hour. Please try again later.");
        await _ai.DidNotReceiveWithAnyArgs().CompleteAsync(null!, Ct);
    }

    [Fact]
    public async Task Sends_only_the_redacted_command_and_uses_the_models_interpretation()
    {
        AiAnswers(new AiCompletion(true, """{"intent":"search_patient","route":"","query":"","uhid":"","phone":"[PHONE_1]","date":"","followUpDays":0,"noteText":""}""", "claude-opus-5", 900, 40, null));

        var response = await Execute(Unrecognised);

        response.Source.ShouldBe("ai");
        response.Intent.ShouldBe(AssistantIntents.SearchPatient);
        response.Parameters["phone"].ShouldBe("+919876543210");
        var sent = (AiRequest)_ai.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IAiGateway.CompleteAsync)).GetArguments()[0]!;
        var prompt = sent.Messages.ShouldHaveSingleItem().Content;
        prompt.ShouldContain("[PHONE_1]");
        prompt.ShouldNotContain("9876543210");
        prompt.ShouldContain("Today: 2026-09-25 (Friday)");
        sent.Purpose.ShouldBe("assistant.command");
        sent.JsonSchema.ShouldNotBeNullOrWhiteSpace();
        await _usage.Received(1).RecordAsync(
            Arg.Is<AiUsageRecord>(u => u.Purpose == "assistant.command" && u.Success && u.Error == null && u.InputTokens == 900 && u.Model == "claude-opus-5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Falls_back_to_the_rules_answer_when_the_model_answer_is_unusable()
    {
        AiAnswers(new AiCompletion(true, "I think you want billing.", "claude-opus-5", 900, 12, null));

        var response = await Execute(Unrecognised);

        response.Intent.ShouldBe(AssistantIntents.Unknown);
        response.Source.ShouldBe("rules");
        await _usage.Received(1).RecordAsync(
            Arg.Is<AiUsageRecord>(u => !u.Success && u.Error == "The answer could not be interpreted."), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Records_provider_failures()
    {
        AiAnswers(new AiCompletion(false, null, "claude-opus-5", 0, 0, "The AI service is busy. Please try again in a minute."));

        var response = await Execute(Unrecognised);

        response.Intent.ShouldBe(AssistantIntents.Unknown);
        await _usage.Received(1).RecordAsync(
            Arg.Is<AiUsageRecord>(u => !u.Success && u.Error == "The AI service is busy. Please try again in a minute."), Arg.Any<CancellationToken>());
    }
}
