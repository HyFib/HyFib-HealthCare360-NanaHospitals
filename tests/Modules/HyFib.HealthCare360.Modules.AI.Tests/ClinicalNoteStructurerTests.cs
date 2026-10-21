using HyFib.HealthCare360.Modules.AI.Features.Clinical;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Clinical;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Integrations;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class ClinicalNoteStructurerTests
{
    private const string Transcript = "Ravi Sharma, 46 year old male, mobile 9876543210, fever for three days with dry cough. Temperature 101 F. Impression viral fever. Paracetamol 650 TDS.";

    private const string Answer = """
        {"subjective":"Fever x 3 days, dry cough.","objective":"T 101 F.","assessment":"Viral fever.","plan":"Tab paracetamol 650 mg TDS.",
         "suggestedIcd":[{"code":"B34.9","title":"Viral infection, unspecified","confidence":0.8}],"warnings":["Duration of paracetamol not dictated."]}
        """;

    private static readonly Guid PatientId = Guid.Parse("0199a1b2-0000-7000-8000-000000000042");

    private readonly AiHarness _ai = new AiHarness().TurnOn();
    private readonly FakeTextProvider _provider = new();

    public ClinicalNoteStructurerTests()
    {
        _ai.Patients.RedactionContextAsync(PatientId, Ct).ReturnsForAnyArgs(new RedactionContext(["Ravi Sharma"], ["NH26000123"]));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<StructureOutcome> Structure() =>
        new ClinicalNoteStructurer(_ai.Router(_provider), _ai.Clock).StructureAsync(new StructureNoteRequest(Transcript, "Internal medicine", PatientId), Ct);

    [Fact]
    public async Task Returns_a_draft_that_always_requires_clinician_review()
    {
        _provider.Answers(Answer);

        var outcome = await Structure();

        outcome.Failure.ShouldBeNull();
        var draft = outcome.Draft.ShouldNotBeNull();
        draft.Status.ShouldBe("draft");
        draft.RequiresClinicianReview.ShouldBeTrue();
        draft.Assessment.ShouldBe("Viral fever.");
        draft.SuggestedIcd.ShouldHaveSingleItem().Code.ShouldBe("B34.9");
        draft.Model.ShouldBe("test-model");
        draft.GeneratedAt.ShouldBe(TestClock.Friday);
        draft.Warnings[0].ShouldBe(ClinicalNoteStructurer.ReviewWarning);
        draft.Warnings.ShouldContain("Duration of paracetamol not dictated.");
    }

    [Fact]
    public async Task Sends_only_the_redacted_dictation_and_says_how_much_was_removed()
    {
        _provider.Answers(Answer);

        var draft = (await Structure()).Draft.ShouldNotBeNull();

        var sent = _provider.Received.ShouldHaveSingleItem().Request;
        var prompt = sent.Messages.ShouldHaveSingleItem().Content;
        prompt.ShouldNotContain("Ravi");
        prompt.ShouldNotContain("Sharma");
        prompt.ShouldNotContain("9876543210");
        prompt.ShouldContain("[NAME_1]");
        prompt.ShouldContain("Specialty: Internal medicine");
        sent.Purpose.ShouldBe("clinical.structure-note");
        await _ai.Patients.Received(1).RedactionContextAsync(PatientId, Arg.Any<CancellationToken>());
        draft.IdentifiersRedacted.ShouldBe(2);
        draft.Warnings.ShouldContain("2 identifier(s) were replaced with placeholders such as [NAME_1] before AI processing.");
    }

    [Fact]
    public async Task Records_usage_for_the_patient_without_any_text()
    {
        _provider.Answers(Answer);

        await Structure();

        var usage = _ai.Recorded.ShouldHaveSingleItem();
        usage.Purpose.ShouldBe("clinical.structure-note");
        usage.Success.ShouldBeTrue();
        usage.PatientId.ShouldBe(PatientId);
        usage.InputTokens.ShouldBe(1200);
        usage.OutputTokens.ShouldBe(300);
    }

    [Fact]
    public async Task Refuses_while_ai_is_turned_off()
    {
        _ai.Setting<bool?>(AiSettingKeys.Enabled, false);

        var outcome = await Structure();

        outcome.Draft.ShouldBeNull();
        outcome.Failure.ShouldBe("AI features are turned off for this facility (setting ai.enabled).");
        _provider.Received.ShouldBeEmpty();
        _ai.Recorded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reports_an_unreadable_draft()
    {
        _provider.Answers("Sure, here is the note: S: fever", 1200, 20);

        var outcome = await Structure();

        outcome.Failure.ShouldBe("The AI service returned a draft HC360 could not read.");
        var usage = _ai.Recorded.ShouldHaveSingleItem();
        usage.Success.ShouldBeFalse();
        usage.Error.ShouldBe("The draft could not be read.");
    }

    [Fact]
    public async Task Passes_on_provider_failures()
    {
        _provider.Answers(new AiCompletion(false, null, "test-model", 0, 0, "The AI service declined this request. Please write the note manually."));

        (await Structure()).Failure.ShouldBe("The AI service declined this request. Please write the note manually.");
    }
}
