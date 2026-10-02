using HyFib.HealthCare360.Integrations.Labels;

namespace HyFib.HealthCare360.Integrations.Tests.Labels;

public sealed class LabelTextTests
{
    [Theory]
    [InlineData("José Ñúñez", "Jose Nunez")]
    [InlineData("EDTA · HAEM", "EDTA - HAEM")]
    [InlineData("Store at 2–6 °C", "Store at 2-6 deg C")]
    [InlineData("Store at −30 °C or colder", "Store at -30 deg C or colder")]
    [InlineData("AB−", "AB-")]
    [InlineData("₹500", "Rs 500")]
    [InlineData("Tab × 10", "Tab x 10")]
    [InlineData("D’Souza “Jr”", "D'Souza \"Jr\"")]
    [InlineData("Smear…", "Smear...")]
    public void Prints_what_staff_type_in_the_printer_fonts_letters(string value, string printed) =>
        LabelText.Printable(value, "The text").ShouldBe(printed);

    [Fact]
    public void Collapses_white_space_and_drops_control_characters() =>
        LabelText.Printable("  WARD\t3\r\nBED  12\u0007 ", "The ward").ShouldBe("WARD 3 BED 12");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_prints_as_nothing(string? value) => LabelText.Printable(value, "The text").ShouldBeEmpty();

    [Theory]
    [InlineData("रवि शर्मा")]
    [InlineData("Ravi 张")]
    public void Refuses_letters_the_printer_fonts_do_not_have(string value) =>
        Should.Throw<LabelTextException>(() => LabelText.Printable(value, "The patient's name"))
            .Message.ShouldBe("The patient's name has letters the label printer's own fonts cannot print.");
}
