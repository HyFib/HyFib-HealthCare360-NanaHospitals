using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Labels;

public sealed class TsplLabelBuilderTests
{
    private static readonly LabelRequest UhidCard = new("UHID00012345", LabelSymbology.Code128, ["SHARMA RAVI", "M/46Y  25-09-2026"], 50, 25, 2);

    [Fact]
    public void Builds_a_code128_label()
    {
        TsplLabelBuilder.Build(UhidCard).ShouldBe(
            "SIZE 50 mm,25 mm\r\n" +
            "GAP 2 mm,0 mm\r\n" +
            "DIRECTION 1\r\n" +
            "REFERENCE 0,0\r\n" +
            "CLS\r\n" +
            "BARCODE 16,16,\"128\",80,1,0,2,2,\"UHID00012345\"\r\n" +
            "TEXT 16,124,\"3\",0,1,1,\"SHARMA RAVI\"\r\n" +
            "TEXT 16,152,\"3\",0,1,1,\"M/46Y  25-09-2026\"\r\n" +
            "PRINT 1,2\r\n");
    }

    [Fact]
    public void Builds_a_qr_label()
    {
        var tspl = TsplLabelBuilder.Build(new LabelRequest("https://portal.example/p/UHID00012345", LabelSymbology.QrCode, ["UHID00012345"]));

        tspl.ShouldContain("QRCODE 16,16,Q,5,A,0,\"https://portal.example/p/UHID00012345\"\r\n");
        tspl.ShouldContain("TEXT 197,16,\"3\",0,1,1,\"UHID00012345\"\r\n");
        tspl.ShouldEndWith("PRINT 1,1\r\n");
    }

    [Fact]
    public void Small_labels_with_many_lines_use_the_smaller_font()
    {
        var tspl = TsplLabelBuilder.Build(UhidCard with { TextLines = ["A", "B", "C"] });

        tspl.ShouldContain("TEXT 16,124,\"2\",0,1,1,\"A\"\r\n");
        tspl.ShouldContain("TEXT 16,148,\"2\",0,1,1,\"B\"\r\n");
    }

    [Theory]
    [InlineData("He said \"hi\"", "He said \\[\"]hi\\[\"]")]
    [InlineData("CR\rLF\n", "CRLF")]
    public void Quotes_data_the_tspl_way(string value, string expected) => TsplLabelBuilder.Quote(value).ShouldBe(expected);

    [Theory]
    [InlineData("zpl", "^XA")]
    [InlineData(" ZPL ", "^XA")]
    [InlineData("tspl", "SIZE")]
    [InlineData("TSPL2", "SIZE")]
    public void The_service_selects_the_printer_language(string language, string prefix) =>
        new LabelPrinterService().Build(UhidCard, language).ShouldStartWith(prefix);

    [Fact]
    public void Unknown_printer_languages_are_refused() =>
        Should.Throw<ArgumentException>(() => new LabelPrinterService().Build(UhidCard, "epl")).Message.ShouldContain("Unsupported printer language");

    [Theory]
    [InlineData("", LabelSymbology.Code128, 50, 25, 1, "required")]
    [InlineData("UHIDé", LabelSymbology.Code128, 50, 25, 1, "printable ASCII")]
    [InlineData("UHID00012345", LabelSymbology.Code128, 5, 25, 1, "between 10 and 150 mm")]
    [InlineData("UHID00012345", LabelSymbology.Code128, 50, 25, 0, "Copies")]
    [InlineData("UHID00012345", LabelSymbology.Code128, 20, 25, 1, "too long for this label width")]
    public void Refuses_labels_that_cannot_print_correctly(string value, LabelSymbology symbology, int width, int height, int copies, string message)
    {
        var request = new LabelRequest(value + (message.Contains("too long", StringComparison.Ordinal) ? new string('9', 36) : string.Empty), symbology, [], width, height, copies);

        Should.Throw<ArgumentException>(() => TsplLabelBuilder.Build(request)).Message.ShouldContain(message);
    }

    [Fact]
    public void Refuses_too_much_text_or_data()
    {
        Should.Throw<ArgumentException>(() => TsplLabelBuilder.Build(UhidCard with { TextLines = Enumerable.Repeat("x", 7).ToList() }));
        Should.Throw<ArgumentException>(() => TsplLabelBuilder.Build(UhidCard with { TextLines = [new string('x', 81)] }));
        Should.Throw<ArgumentException>(() => TsplLabelBuilder.Build(new LabelRequest(new string('x', 401), LabelSymbology.QrCode, [])));
        Should.Throw<ArgumentException>(() => TsplLabelBuilder.Build(new LabelRequest(new string('x', 49), LabelSymbology.Code128, [])));
    }
}
