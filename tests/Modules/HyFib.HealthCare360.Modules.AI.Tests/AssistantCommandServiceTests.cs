using HyFib.HealthCare360.Modules.AI.Features.Assistant;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Assistant;
using HyFib.HealthCare360.Platform.Integrations;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class AssistantCommandServiceTests
{
    private const string Unrecognised = "who was the last patient I saw, the one with 9876543210";

    private static readonly Guid FoundPatient = Guid.Parse("0199a1b2-0000-7000-8000-000000000001");

    private readonly AiHarness _ai = new AiHarness().TurnOn();
    private readonly FakeTextProvider _provider = new();
    private readonly IAiQuota _quota = Substitute.For<IAiQuota>();

    public AssistantCommandServiceTests()
    {
        // NSubstitute would otherwise answer "" (an auto value), which reads as "over quota".
        _quota.CheckAsync(Ct).ReturnsForAnyArgs((string?)null);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AssistantCommandService Service => new(_ai.Router(_provider), _quota, _ai.Patients, _ai.Clock);

    private Task<AssistantCommandResponse> Execute(string text, Guid? patientId = null) =>
        Service.ExecuteAsync(new AssistantCommandRequest(text, new AssistantContext("/emr", patientId)), Ct);

    [Fact]
    public async Task Understood_commands_never_reach_the_ai_provider()
    {
        var response = await Execute("go to billing");

        response.Intent.ShouldBe(AssistantIntents.Navigate);
        response.Source.ShouldBe("rules");
        response.Parameters["route"].ShouldBe("/billing");
        response.ConfirmationText.ShouldBe("Opening Billing.");
        _provider.Received.ShouldBeEmpty();
        await _quota.DidNotReceiveWithAnyArgs().CheckAsync(Ct);
        _ai.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolves_a_spoken_uhid_to_the_patient_record()
    {
        _ai.Patients.FindByUhidAsync("NH26000123", Ct).ReturnsForAnyArgs(FoundPatient);

        var response = await Execute("open patient UHID NH26000123");

        response.Intent.ShouldBe(AssistantIntents.OpenPatient);
        response.Parameters["patientId"].ShouldBe(FoundPatient.ToString());
        response.Parameters["patientFound"].ShouldBe("true");
        response.ConfirmationText.ShouldBe("Opening patient NH26000123.");
    }

    [Fact]
    public async Task Says_so_when_no_patient_has_the_uhid()
    {
        _ai.Patients.FindByUhidAsync("NH26000999", Ct).ReturnsForAnyArgs((Guid?)null);

        var response = await Execute("open patient UHID NH26000999");

        response.Parameters["patientFound"].ShouldBe("false");
        response.Parameters.ContainsKey("patientId").ShouldBeFalse();
        response.ConfirmationText.ShouldBe("No patient was found with UHID NH26000999.");
    }

    [Fact]
    public async Task Stays_offline_when_ai_is_turned_off()
    {
        _ai.Setting<bool?>(AiSettingKeys.Enabled, false);

        var response = await Execute(Unrecognised);

        response.Intent.ShouldBe(AssistantIntents.Unknown);
        response.Source.ShouldBe("rules");
        _provider.Received.ShouldBeEmpty();
        _ai.Recorded.ShouldBeEmpty();
        await _quota.DidNotReceiveWithAnyArgs().CheckAsync(Ct);
    }

    [Fact]
    public async Task Explains_when_the_user_is_over_quota()
    {
        _quota.CheckAsync(Ct).ReturnsForAnyArgs("You have reached the limit of 60 AI requests per hour. Please try again later.");

        var response = await Execute(Unrecognised);

        response.ConfirmationText.ShouldBe("You have reached the limit of 60 AI requests per hour. Please try again later.");
        _provider.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sends_only_the_redacted_command_and_uses_the_models_interpretation()
    {
        _provider.Answers("""{"intent":"search_patient","route":"","query":"","uhid":"","phone":"[PHONE_1]","date":"","followUpDays":0,"noteText":""}""", 900, 40);

        var response = await Execute(Unrecognised);

        response.Source.ShouldBe("ai");
        response.Intent.ShouldBe(AssistantIntents.SearchPatient);
        response.Parameters["phone"].ShouldBe("+919876543210");
        var sent = _provider.Received.ShouldHaveSingleItem().Request;
        var prompt = sent.Messages.ShouldHaveSingleItem().Content;
        prompt.ShouldContain("[PHONE_1]");
        prompt.ShouldNotContain("9876543210");
        prompt.ShouldContain("Today: 2026-09-25 (Friday)");
        sent.Purpose.ShouldBe("assistant.command");
        sent.JsonSchema.ShouldNotBeNullOrWhiteSpace();
        var usage = _ai.Recorded.ShouldHaveSingleItem();
        usage.Purpose.ShouldBe("assistant.command");
        usage.Provider.ShouldBe("anthropic");
        usage.Model.ShouldBe("test-model");
        usage.InputTokens.ShouldBe(900);
        usage.Success.ShouldBeTrue();
        usage.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Falls_back_to_the_rules_answer_when_the_model_answer_is_unusable()
    {
        _provider.Answers("I think you want billing.", 900, 12);

        var response = await Execute(Unrecognised);

        response.Intent.ShouldBe(AssistantIntents.Unknown);
        response.Source.ShouldBe("rules");
        var usage = _ai.Recorded.ShouldHaveSingleItem();
        usage.Success.ShouldBeFalse();
        usage.Error.ShouldBe("The answer could not be interpreted.");
    }

    [Fact]
    public async Task Records_provider_failures()
    {
        _provider.Answers(new AiCompletion(false, null, "test-model", 0, 0, "The AI service is busy. Please try again in a minute."));

        var response = await Execute(Unrecognised);

        response.Intent.ShouldBe(AssistantIntents.Unknown);
        var usage = _ai.Recorded.ShouldHaveSingleItem();
        usage.Success.ShouldBeFalse();
        usage.Error.ShouldBe("The AI service is busy. Please try again in a minute.");
    }
}
