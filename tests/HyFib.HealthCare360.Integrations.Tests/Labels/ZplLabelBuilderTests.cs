using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Labels;

public sealed class ZplLabelBuilderTests
{
    private static readonly LabelRequest UhidCard = new("UHID00012345", LabelSymbology.Code128, ["SHARMA RAVI", "M/46Y  25-09-2026"], 50, 25, 2);

    [Fact]
    public void Builds_a_code128_label_at_203_dpi()
    {
        ZplLabelBuilder.Build(UhidCard).ShouldBe(
            "^XA\n" +
            "^CI28\n" +
            "^PW400\n" +
            "^LL200\n" +
            "^LH0,0\n" +
            "^FO16,16^BY2,3,80^BCN,80,Y,N,N,A^FH^FDUHID00012345^FS\n" +
            "^FO16,124^A0N,30,30^FH^FDSHARMA RAVI^FS\n" +
            "^FO16,156^A0N,30,30^FH^FDM/46Y  25-09-2026^FS\n" +
            "^PQ2\n" +
            "^XZ\n");
    }

    [Fact]
    public void Builds_a_qr_label_with_text_beside_the_code()
    {
        var request = new LabelRequest("https://portal.example/p/UHID00012345", LabelSymbology.QrCode, ["UHID00012345"]);

        var zpl = ZplLabelBuilder.Build(request);

        // 37 bytes need QR version 4 (33 modules) at level Q; 168 printable dots / 33 = magnification 5.
        zpl.ShouldContain("^FO16,16^BQN,2,5^FH^FDQA,https://portal.example/p/UHID00012345^FS\n");
        zpl.ShouldContain("^FO197,16^A0N,32,32^FH^FDUHID00012345^FS\n");
        zpl.ShouldContain("^PQ1\n");
    }

    [Theory]
    [InlineData("A^B~C_D", "A_5EB_7EC_5FD")]
    [InlineData("José", "Jos_C3_A9")]
    [InlineData("₹500", "_E2_82_B9500")]
    [InlineData("line\nbreak", "linebreak")]
    public void Escapes_field_data_so_it_can_never_become_a_command(string value, string expected) =>
        ZplLabelBuilder.Escape(value).ShouldBe(expected);

    [Fact]
    public void Injected_commands_stay_inside_the_field()
    {
        var zpl = ZplLabelBuilder.Build(UhidCard with { TextLines = ["^XZ^XA^FO0,0^GB800,800,800^FS"] });

        zpl.ShouldContain("^FH^FD_5EXZ_5EXA_5EFO0,0_5EGB800,800,800_5EFS^FS");
        zpl.Split('\n').Count(line => line == "^XZ").ShouldBe(1);
    }

    [Fact]
    public void Drops_blank_lines_and_control_characters()
    {
        var zpl = ZplLabelBuilder.Build(UhidCard with { TextLines = ["  ", "WARD\t3"] });

        zpl.ShouldContain("^FDWARD3^FS");
        zpl.Split('\n').Count(line => line.Contains("^A0N", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Theory]
    [InlineData(2.0, 16)]
    [InlineData(10.0, 80)]
    [InlineData(25.4, 203)]
    [InlineData(50.0, 400)]
    public void Converts_millimetres_to_dots(double millimetres, int dots) => LabelLayout.Dots(millimetres).ShouldBe(dots);

    [Theory]
    [InlineData(11, 21)]
    [InlineData(12, 25)]
    [InlineData(400, 93)]
    public void Chooses_the_smallest_qr_version_for_the_data(int bytes, int modules) =>
        LabelLayout.QrModules(new string('x', bytes)).ShouldBe(modules);
}
