using HyFib.HealthCare360.Modules.AI.Features.Assistant;
using HyFib.HealthCare360.Modules.AI.Features.Clinical;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class ContractValidatorTests
{
    private static IEnumerable<string> Errors(AssistantCommandRequest request) =>
        new AssistantCommandRequestValidator().Validate(request).Errors.Select(e => e.ErrorMessage);

    private static IEnumerable<string> Errors(StructureNoteRequest request) =>
        new StructureNoteRequestValidator().Validate(request).Errors.Select(e => e.ErrorMessage);

    [Fact]
    public void Accepts_a_spoken_command_with_its_screen_context() =>
        Errors(new AssistantCommandRequest("open patient UHID NH26000123", new AssistantContext("/emr", Guid.NewGuid()))).ShouldBeEmpty();

    [Fact]
    public void A_command_is_required() =>
        Errors(new AssistantCommandRequest("", null)).ShouldContain("Say or type a command.");

    [Fact]
    public void Commands_are_short_and_free_of_control_characters()
    {
        Errors(new AssistantCommandRequest(new string('a', 501), null)).ShouldNotBeEmpty();
        Errors(new AssistantCommandRequest("go to billing\u001b[2J", null)).ShouldContain("Contains characters that are not allowed.");
        Errors(new AssistantCommandRequest("go to billing", new AssistantContext("/emr\u0000", null))).ShouldContain("Contains characters that are not allowed.");
    }

    [Fact]
    public void Accepts_a_multi_line_dictation() =>
        Errors(new StructureNoteRequest("Fever for three days.\nNo cough.\r\nImpression viral fever.", "General medicine", null)).ShouldBeEmpty();

    [Theory]
    [InlineData("", "The dictation is empty.")]
    [InlineData("fever", "The dictation is too short to structure.")]
    public void Rejects_dictations_too_short_to_structure(string transcript, string message) =>
        Errors(new StructureNoteRequest(transcript, null, null)).ShouldContain(message);

    [Fact]
    public void Bounds_the_dictation_and_specialty()
    {
        Errors(new StructureNoteRequest(new string('a', 20_001), null, null)).ShouldNotBeEmpty();
        Errors(new StructureNoteRequest("Fever for three days.", new string('s', 101), null)).ShouldNotBeEmpty();
    }
}
