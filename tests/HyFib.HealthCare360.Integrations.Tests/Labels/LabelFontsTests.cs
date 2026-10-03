using HyFib.HealthCare360.Integrations.Labels;

namespace HyFib.HealthCare360.Integrations.Tests.Labels;

public sealed class LabelFontsTests
{
    [Theory]
    [InlineData(20, "2", 1, 20)]
    [InlineData(17, "2", 1, 20)] // 20 is nearer than 12 and less than a quarter taller
    [InlineData(15, "1", 1, 12)] // 20 would be a third taller
    [InlineData(24, "3", 1, 24)] // the font at its own size before font 1 magnified twice
    [InlineData(29, "4", 1, 32)]
    [InlineData(40, "2", 2, 40)]
    [InlineData(5, "1", 1, 12)] // nothing smaller: the smallest font
    public void Tspl_takes_the_dot_font_nearest_in_height(int height, string font, int multiplier, int printed)
    {
        var picked = LabelFonts.Pick(LabelLanguage.Tspl, height);

        picked.Name.ShouldBe(font);
        picked.Multiplier.ShouldBe(multiplier);
        picked.Height.ShouldBe(printed);
    }

    [Fact]
    public void Zpl_scales_font_0_to_the_height() =>
        LabelFonts.Pick(LabelLanguage.Zpl, 33).ShouldBe(new LabelFont("0", 1, 33, 33));

    [Fact]
    public void Tspl_text_is_as_wide_as_its_cells_plus_a_dot() =>
        LabelFonts.Measure(LabelLanguage.Tspl, "ABCD", 20).ShouldBe(4 * 13);

    [Fact]
    public void Zpl_widths_follow_the_letters() =>
        LabelFonts.Measure(LabelLanguage.Zpl, "WWWW", 20).ShouldBeGreaterThan(LabelFonts.Measure(LabelLanguage.Zpl, "iiii", 20) * 2);

    [Theory]
    [InlineData(LabelLanguage.Zpl)]
    [InlineData(LabelLanguage.Tspl)]
    public void Fitting_keeps_text_that_fits_and_shortens_text_that_does_not(LabelLanguage language)
    {
        LabelFonts.Fit(language, "SHARMA", 20, 400).ShouldBe("SHARMA");

        var fitted = LabelFonts.Fit(language, "VENKATARAMANAN SUBRAMANIAM IYER", 20, 200);
        fitted.ShouldEndWith("...");
        LabelFonts.Measure(language, fitted, 20).ShouldBeLessThanOrEqualTo(200);
        "VENKATARAMANAN SUBRAMANIAM IYER".ShouldStartWith(fitted[..^3]);
    }

    [Fact]
    public void Wrapping_breaks_between_words_and_marks_what_is_left_over()
    {
        var lines = LabelFonts.Wrap(LabelLanguage.Tspl, "1 tab twice a day for 5 days after food with water", 20, 13 * 20, 2);

        lines.Count.ShouldBe(2);
        lines[0].ShouldBe("1 tab twice a day"); // "for" would make 21 characters, one more than 20 cells hold
        lines[1].ShouldStartWith("for 5 days");
        lines[1].ShouldEndWith("...");
        lines.ShouldAllBe(line => LabelFonts.Measure(LabelLanguage.Tspl, line, 20) <= 13 * 20);
    }

    [Fact]
    public void Wrapping_short_text_gives_one_line() =>
        LabelFonts.Wrap(LabelLanguage.Zpl, "after food", 20, 400, 2).ShouldBe(["after food"]);
}
