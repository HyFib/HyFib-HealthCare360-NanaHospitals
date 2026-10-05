using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Integrations.Labels.Templates;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Labels;

public sealed class LabelWriterTests
{
    [Fact]
    public void Writes_a_sample_tube_label_in_zpl_at_203_dpi() =>
        LabelRenderer.Render(LabelSamples.Label(), [LabelSamples.Tube(stat: true)], 1, LabTubeTemplate.Draw).ShouldBe(
            "^XA\n" +
            "^CI28\n" +
            "^PW400\n" +
            "^LL200\n" +
            "^LH0,0\n" +
            "^FO12,12^A0N,22,22^FH^FDSharma Ravi Kumar^FS\n" +
            "^FO12,17^A0N,17,17^FB376,1,0,R,0^FH^FD46Y/M^FS\n" +
            "^FO341,38^A0N,17,17^FH^FDSTAT^FS\n" +
            "^FO338,35^FR^GB50,23,23^FS\n" +
            "^FO12,38^A0N,17,17^FH^FDUHID NH00012345^FS\n" +
            "^FO77,62^BY2^BCN,80,N,N,N,N^FH^FD>:LS>52600000123^FS\n" +
            "^FO12,146^A0N,17,17^FB376,1,0,R,0^FH^FDEDTA - HAEM^FS\n" +
            "^FO12,146^A0N,17,17^FH^FDLS2600000123^FS\n" +
            "^FO12,167^A0N,17,17^FB376,1,0,R,0^FH^FD26 Sep 10:32^FS\n" +
            "^FO12,167^A0N,17,17^FH^FDCBC, ESR, Peripheral smear^FS\n" +
            "^PQ1\n" +
            "^XZ\n");

    [Fact]
    public void Writes_a_sample_tube_label_in_tspl_at_203_dpi() =>
        LabelRenderer.Render(LabelSamples.Label(LabelLanguage.Tspl), [LabelSamples.Tube(stat: true)], 1, LabTubeTemplate.Draw).ShouldBe(
            "SIZE 50 mm,25 mm\r\n" +
            "GAP 2 mm,0 mm\r\n" +
            "DIRECTION 1\r\n" +
            "REFERENCE 0,0\r\n" +
            "CLS\r\n" +
            "TEXT 12,12,\"2\",0,1,1,\"Sharma Ravi Kumar\"\r\n" +
            "TEXT 323,12,\"2\",0,1,1,\"46Y/M\"\r\n" +
            "TEXT 333,36,\"2\",0,1,1,\"STAT\"\r\n" +
            "REVERSE 330,33,58,26\r\n" +
            "TEXT 12,36,\"2\",0,1,1,\"UHID NH00012345\"\r\n" +
            "BARCODE 77,63,\"128M\",73,0,0,2,2,\"!104LS!0992600000123\"\r\n" +
            "TEXT 245,140,\"2\",0,1,1,\"EDTA - HAEM\"\r\n" +
            "TEXT 12,140,\"2\",0,1,1,\"LS2600000123\"\r\n" +
            "TEXT 232,164,\"2\",0,1,1,\"26 Sep 10:32\"\r\n" +
            "TEXT 12,164,\"2\",0,1,1,\"CBC, ESR +1\"\r\n" +
            "PRINT 1,1\r\n");

    [Fact]
    public void Each_label_is_its_own_format_with_its_copies()
    {
        var zpl = LabelRenderer.Render(LabelSamples.Label(), [LabelSamples.Tube(), LabelSamples.Tube()], 3, LabTubeTemplate.Draw);
        var tspl = LabelRenderer.Render(LabelSamples.Label(LabelLanguage.Tspl), [LabelSamples.Tube(), LabelSamples.Tube()], 3, LabTubeTemplate.Draw);

        zpl.Split('\n').Count(line => line == "^XA").ShouldBe(2);
        zpl.Split('\n').Count(line => line == "^PQ3").ShouldBe(2);
        tspl.Split("\r\n").Count(line => line == "SIZE 50 mm,25 mm").ShouldBe(1);
        tspl.Split("\r\n").Count(line => line == "CLS").ShouldBe(2);
        tspl.Split("\r\n").Count(line => line == "PRINT 1,3").ShouldBe(2);
    }

    [Theory]
    [InlineData("A^B~C_D", "A_5EB_7EC_5FD")]
    [InlineData("José", "Jos_C3_A9")]
    [InlineData("₹500", "_E2_82_B9500")]
    [InlineData("line\nbreak", "linebreak")]
    public void Zpl_field_data_is_hex_escaped_so_it_can_never_become_a_command(string value, string expected) =>
        ZplWriter.Escape(value).ShouldBe(expected);

    [Theory]
    [InlineData("He said \"hi\"", "He said \\[\"]hi\\[\"]")]
    [InlineData("CR\rLF\n", "CRLF")]
    [InlineData("Ward 3\\4", "Ward 3/4")]
    public void Tspl_strings_are_quoted_the_tspl_way(string value, string expected) => TsplWriter.Quote(value).ShouldBe(expected);

    [Fact]
    public void Commands_typed_into_a_zpl_label_stay_inside_their_field()
    {
        var tube = LabelSamples.Tube() with { PatientName = "^XZ^XA^PQ99", Tests = ["~JR"] };

        var zpl = LabelRenderer.Render(LabelSamples.Label(), [tube], 1, LabTubeTemplate.Draw);

        zpl.ShouldContain("^FH^FD_5EXZ_5EXA_5EPQ99^FS");
        zpl.ShouldContain("^FD_7EJR^FS");
        zpl.Split('\n').Count(line => line == "^XZ").ShouldBe(1);
        zpl.Split('\n').ShouldNotContain(line => line.Contains("^PQ99", StringComparison.Ordinal));
        zpl.ShouldNotContain("~JR");
    }

    [Fact]
    public void Commands_typed_into_a_tspl_label_stay_inside_their_string()
    {
        var tube = LabelSamples.Tube() with { PatientName = "x\"\r\nPRINT 1,99\r\nTEXT 0,0,\"" };

        var tspl = LabelRenderer.Render(LabelSamples.Label(LabelLanguage.Tspl), [tube], 1, LabTubeTemplate.Draw);

        tspl.Split("\r\n").ShouldNotContain(line => line.StartsWith("PRINT 1,99", StringComparison.Ordinal));
        tspl.ShouldContain("\"x\\[\"] PRINT 1,99 TEXT");
    }

    [Fact]
    public void The_general_label_keeps_its_platform_contract()
    {
        var service = new LabelPrinterService();
        var request = new LabelRequest("UHID00012345", LabelSymbology.Code128, ["SHARMA RAVI", "M/46Y  25-09-2026"], 50, 25, 2);

        var zpl = service.Build(request, " ZPL ");
        zpl.ShouldStartWith("^XA\n");
        zpl.ShouldContain("^BCN,");
        zpl.ShouldContain("^FD>:UHID>500012345^FS");
        zpl.ShouldContain("^FDSHARMA RAVI^FS");
        zpl.ShouldContain("^PQ2\n");

        var tspl = service.Build(request with { Dpi = 300 }, "TSPL2");
        tspl.ShouldStartWith("SIZE 50 mm,25 mm\r\n");
        tspl.ShouldContain("\"!104UHID!09900012345\"");
        tspl.ShouldEndWith("PRINT 1,2\r\n");
    }

    [Fact]
    public void The_general_label_puts_text_beside_a_qr_code()
    {
        var request = new LabelRequest("https://portal.example/p/UHID00012345", LabelSymbology.QrCode, ["UHID00012345"]);

        var zpl = new LabelPrinterService().Build(request, "zpl");

        // 37 bytes need QR version 4 (33 modules) at level Q; 168 printable dots / 33 = magnification 5.
        zpl.ShouldContain("^FO16,16^BQN,2,5^FH^FDQA,https://portal.example/p/UHID00012345^FS\n");
        zpl.ShouldContain("^FDUHID00012345^FS");
    }

    [Theory]
    [InlineData("", LabelSymbology.Code128, 50, 25, 1, 203, "required")]
    [InlineData("UHIDé", LabelSymbology.Code128, 50, 25, 1, 203, "printable ASCII")]
    [InlineData("UHID00012345", LabelSymbology.Code128, 5, 25, 1, 203, "between 10 and 150 mm")]
    [InlineData("UHID00012345", LabelSymbology.Code128, 50, 25, 0, 203, "Copies")]
    [InlineData("UHID00012345", LabelSymbology.Code128, 50, 25, 1, 600, "203 or 300 dpi")]
    [InlineData("UHIDABCDEFGHIJKLMNOPQRSTUVWX", LabelSymbology.Code128, 20, 25, 1, 203, "too long to print as a barcode")]
    public void The_general_label_refuses_labels_that_cannot_print_correctly(
        string value, LabelSymbology symbology, int width, int height, int copies, int dpi, string message)
    {
        var request = new LabelRequest(value, symbology, [], width, height, copies, dpi);

        Should.Throw<ArgumentException>(() => new LabelPrinterService().Build(request, "zpl")).Message.ShouldContain(message);
    }

    [Fact]
    public void Unknown_printer_languages_are_refused() =>
        Should.Throw<ArgumentException>(() => new LabelPrinterService().Build(new LabelRequest("X1", LabelSymbology.Code128, []), "epl"))
            .Message.ShouldContain("Unsupported printer language");
}
