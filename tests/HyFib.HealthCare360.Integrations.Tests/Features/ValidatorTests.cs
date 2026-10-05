using FluentValidation;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Integrations.Features.Labels;
using HyFib.HealthCare360.Integrations.Labels;
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
        Invalid(Label with { Value = "UHID!0001" }).ShouldBe(["Value"]);
        Invalid(Label with { Dpi = 600 }).ShouldBe(["Dpi"]);
    }

    private static readonly LabelPrinterSetup Zebra = new("zpl", 203, 50, 25);
    private static readonly LabelPrinterSetup BandPrinter = new("zpl", 300, 25, 254);

    private static IEnumerable<string> Invalid<T>(AbstractValidator<T> validator, T request) =>
        validator.Validate(request).Errors.Select(e => e.PropertyName);

    [Fact]
    public void A_typical_tube_label_request_is_valid() =>
        Invalid(new LabTubeLabelsRequestValidator(), new LabTubeLabelsRequest(Zebra, [Labels.LabelSamples.Tube()])).ShouldBeEmpty();

    [Fact]
    public void Tube_label_requests_need_a_label_printer_and_readable_barcodes()
    {
        var validator = new LabTubeLabelsRequestValidator();
        var request = new LabTubeLabelsRequest(Zebra, [Labels.LabelSamples.Tube()]);

        Invalid(validator, request with { Printer = Zebra with { Language = "epl", Dpi = 600, WidthMm = 20, HeightMm = 10.25m } })
            .ShouldBe(["Printer.Language", "Printer.Dpi", "Printer.WidthMm", "Printer.HeightMm"], ignoreOrder: true);
        Invalid(validator, request with { Labels = [] }).ShouldBe(["Labels"]);
        Invalid(validator, request with { Labels = Enumerable.Repeat(Labels.LabelSamples.Tube(), 101).ToList() }).ShouldBe(["Labels"]);
        Invalid(validator, request with { Labels = [Labels.LabelSamples.Tube() with { Barcode = "LS!26" }] }).ShouldBe(["Labels[0].Barcode"]);
        Invalid(validator, request with { Labels = [Labels.LabelSamples.Tube() with { PatientName = "Ravi\u001b[2J" }] }).ShouldBe(["Labels[0].PatientName"]);
        Invalid(validator, request with { Copies = 11 }).ShouldBe(["Copies"]);
    }

    [Fact]
    public void Wristbands_print_on_band_sizes_not_label_sizes()
    {
        var validator = new WristbandLabelsRequestValidator();
        var request = new WristbandLabelsRequest(BandPrinter, [Labels.LabelSamples.Band()], WristbandCode.QrCode);

        Invalid(validator, request).ShouldBeEmpty();
        Invalid(validator, request with { Printer = Zebra }).ShouldBe(["Printer.WidthMm", "Printer.HeightMm"], ignoreOrder: true);
        Invalid(validator, request with { Labels = [Labels.LabelSamples.Band() with { Uhid = "" }] }).ShouldBe(["Labels[0].Uhid"]);
        Invalid(validator, request with { Labels = Enumerable.Repeat(Labels.LabelSamples.Band(), 11).ToList() }).ShouldBe(["Labels"]);
    }

    [Fact]
    public void The_other_label_requests_check_their_own_fields()
    {
        Invalid(new DispensingLabelsRequestValidator(), new DispensingLabelsRequest(Zebra, [Labels.LabelSamples.Medicine() with { Quantity = "" }]))
            .ShouldBe(["Labels[0].Quantity"]);
        Invalid(new BloodUnitLabelsRequestValidator(), new BloodUnitLabelsRequest(Zebra, [Labels.LabelSamples.Unit() with { VolumeMl = 0 }]))
            .ShouldBe(["Labels[0].VolumeMl"]);
        Invalid(new CssdPackLabelsRequestValidator(), new CssdPackLabelsRequest(Zebra, [Labels.LabelSamples.Pack() with { PackNumber = "CS>1" }]))
            .ShouldBe(["Labels[0].PackNumber"]);
        Invalid(new AssetTagLabelsRequestValidator(), new AssetTagLabelsRequest(Zebra, [Labels.LabelSamples.Asset() with { AssetName = "" }]))
            .ShouldBe(["Labels[0].AssetName"]);
    }

    [Fact]
    public void A_test_label_checks_the_sizes_of_its_stock()
    {
        var validator = new TestLabelRequestValidator();

        Invalid(validator, new TestLabelRequest(Zebra, LabelStock.Label, "OPD-1")).ShouldBeEmpty();
        Invalid(validator, new TestLabelRequest(BandPrinter, LabelStock.Wristband, "WARD-3")).ShouldBeEmpty();
        Invalid(validator, new TestLabelRequest(Zebra, LabelStock.Wristband, "WARD-3")).ShouldBe(["Printer.WidthMm", "Printer.HeightMm"], ignoreOrder: true);
    }

    [Fact]
    public void A_printer_change_names_the_computer_and_each_kind_of_printer_once()
    {
        var validator = new PrinterSetupChangeValidator();
        var change = new PrinterSetupChange("OPD-RECEPTION-2", Guid.NewGuid(),
        [
            new PrinterRoleSetup("labels", "ZDesigner GK420t", "zpl", 203, 50, 25),
            new PrinterRoleSetup("wristbands", "ZDesigner HC100", "zpl", 300, 25, 254, "qr"),
            new PrinterRoleSetup("receipts", "EPSON TM-T82 Receipt", null, null, null, null),
            new PrinterRoleSetup("documents", "HP LaserJet M404", null, null, null, null),
        ]);

        Invalid(validator, change).ShouldBeEmpty();
        Invalid(validator, change with { Computer = "" }).ShouldBe(["Computer"]);
        Invalid(validator, change with { Roles = [change.Roles[0], change.Roles[0]] }).ShouldBe(["Roles"]);
        Invalid(validator, change with { Roles = [change.Roles[0] with { Role = "faxes" }] }).ShouldBe(["Roles[0].Role"]);
        Invalid(validator, change with { Roles = [change.Roles[0] with { Language = "epl", Dpi = 600 }] })
            .ShouldBe(["Roles[0].Language", "Roles[0].Dpi"], ignoreOrder: true);
        Invalid(validator, change with { Roles = [change.Roles[0] with { Printer = "Zebra\u0000" }] }).ShouldBe(["Roles[0].Printer"]);
        Invalid(validator, change with { Roles = [change.Roles[1] with { Code = "pdf417" }] }).ShouldBe(["Roles[0].Code"]);
        Invalid(validator, change with { Roles = [change.Roles[0] with { Code = "qr" }] }).ShouldBe(["Roles[0].Code"]);
    }
}
