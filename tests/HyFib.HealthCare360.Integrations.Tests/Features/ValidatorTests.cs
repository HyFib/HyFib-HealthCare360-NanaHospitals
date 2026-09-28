using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Integrations.Features.Labels;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Features;

public sealed class ValidatorTests
{
    private static readonly PrintLabelRequest Label = new("UHID00012345", LabelSymbology.Code128, ["SHARMA RAVI"]);

    private static IEnumerable<string> Invalid(TestIntegrationRequest request) =>
        new TestIntegrationRequestValidator().Validate(request).Errors.Select(e => e.PropertyName);

    private static IEnumerable<string> Invalid(PrintLabelRequest request) =>
        new PrintLabelRequestValidator().Validate(request).Errors.Select(e => e.PropertyName);

    [Fact]
    public void An_empty_test_request_is_valid() => Invalid(new TestIntegrationRequest(null, null, null, null, null)).ShouldBeEmpty();

    [Fact]
    public void Accepts_well_formed_test_inputs() =>
        Invalid(new TestIntegrationRequest("+919876543210", "Test from HC360", "1107160000000012345", "appointment_confirmed", "en_US")).ShouldBeEmpty();

    [Theory]
    [InlineData("12345", null, null, "DltTemplateId")]
    [InlineData(null, "Appointment Confirmed", null, "TemplateName")]
    [InlineData(null, null, "english", "LanguageCode")]
    public void Rejects_malformed_provider_identifiers(string? dlt, string? template, string? language, string property) =>
        Invalid(new TestIntegrationRequest(null, null, dlt, template, language)).ShouldBe([property]);

    [Fact]
    public void Rejects_control_characters_in_free_text() =>
        Invalid(new TestIntegrationRequest("ravi\u0000@example.com", "hi\u001b[2J", null, null, null)).ShouldBe(["To", "Message"], ignoreOrder: true);

    [Fact]
    public void A_typical_label_is_valid() => Invalid(Label).ShouldBeEmpty();

    [Theory]
    [InlineData(" ZPL ")]
    [InlineData("tspl")]
    public void Accepts_supported_printer_languages(string language) => Invalid(Label with { Language = language }).ShouldBeEmpty();

    [Fact]
    public void Qr_codes_may_carry_unicode_but_code128_may_not()
    {
        Invalid(Label with { Symbology = LabelSymbology.QrCode, Value = "रवि शर्मा UHID00012345" }).ShouldBeEmpty();
        Invalid(Label with { Value = "रवि" }).ShouldBe(["Value"]);
        Invalid(Label with { Value = new string('9', 49) }).ShouldBe(["Value"]);
    }

    [Fact]
    public void Rejects_labels_the_printer_cannot_produce()
    {
        Invalid(Label with { Language = "epl" }).ShouldBe(["Language"]);
        Invalid(Label with { WidthMm = 5 }).ShouldBe(["WidthMm"]);
        Invalid(Label with { Copies = 0 }).ShouldBe(["Copies"]);
        Invalid(Label with { TextLines = Enumerable.Repeat("x", 7).ToList() }).ShouldBe(["TextLines"]);
        Invalid(Label with { TextLines = ["ok", "bad\u0007"] }).ShouldBe(["TextLines[1]"]);
        Invalid(Label with { Value = "" }).ShouldBe(["Value"]);
    }
}
