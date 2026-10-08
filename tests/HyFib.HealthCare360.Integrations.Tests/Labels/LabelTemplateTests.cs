using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Integrations.Labels.Templates;

namespace HyFib.HealthCare360.Integrations.Tests.Labels;

/// <summary>
/// Every label lays out on its usual stock in both languages at both resolutions: everything inside the label (the
/// canvas refuses anything outside it), barcodes with modules of whole dots no narrower than 0.19 mm and with their
/// quiet zones, and the marks staff rely on (STAT, ALLERGY, NOT FOR ISSUE, NOT RELEASED) in white on black.
/// </summary>
public sealed class LabelTemplateTests
{
    public static TheoryData<LabelLanguage, int> Printers => LabelSamples.Printers();

    private static LabelCanvas Draw<T>(LabelMedia media, T label, Action<LabelCanvas, T> draw)
    {
        var canvas = new LabelCanvas(media);
        draw(canvas, label);
        return canvas;
    }

    private static void BarcodesScan(LabelCanvas canvas)
    {
        var narrowest = (int)Math.Ceiling(0.19 * canvas.Media.Dpi / 25.4);
        foreach (var barcode in canvas.Elements.OfType<BarcodeElement>())
        {
            barcode.ModuleWidth.ShouldBeGreaterThanOrEqualTo(narrowest);
            var (start, end, size) = barcode.Rotation == LabelRotation.Normal
                ? (barcode.X, barcode.X + barcode.Length, canvas.Width)
                : (barcode.Y, barcode.Y + barcode.Length, canvas.Height);
            start.ShouldBeGreaterThanOrEqualTo(10 * barcode.ModuleWidth, "quiet zone before the barcode");
            (size - end).ShouldBeGreaterThanOrEqualTo(10 * barcode.ModuleWidth, "quiet zone after the barcode");
        }
    }

    [Theory]
    [MemberData(nameof(Printers))]
    public void A_sample_tube_label_fits_with_its_barcode_and_the_stat_mark(LabelLanguage language, int dpi)
    {
        var stat = Draw(LabelSamples.Label(language, dpi), LabelSamples.Tube(stat: true), LabTubeTemplate.Draw);
        var routine = Draw(LabelSamples.Label(language, dpi), LabelSamples.Tube(), LabTubeTemplate.Draw);

        BarcodesScan(stat);
        stat.Elements.OfType<BarcodeElement>().ShouldHaveSingleItem().Plan.Value.ShouldBe("LS2600000123");
        stat.Elements.OfType<ReverseElement>().ShouldHaveSingleItem();
        stat.Elements.OfType<TextElement>().ShouldContain(t => t.Text == "STAT");
        routine.Elements.OfType<ReverseElement>().ShouldBeEmpty();
        routine.Elements.OfType<TextElement>().ShouldContain(t => t.Text == "LA2600123");
        routine.Elements.OfType<TextElement>().ShouldContain(t => t.Text == "26 Sep 10:32");
        routine.Elements.OfType<TextElement>().ShouldContain(t => t.Text == "EDTA - HAEM");
    }

    [Theory]
    [InlineData(38, 25)]
    [InlineData(50, 38)]
    [InlineData(75, 50)]
    [InlineData(100, 50)]
    public void A_sample_tube_label_fits_other_label_sizes(int width, int height)
    {
        foreach (var language in new[] { LabelLanguage.Zpl, LabelLanguage.Tspl })
        {
            foreach (var dpi in new[] { 203, 300 })
            {
                BarcodesScan(Draw(LabelSamples.Label(language, dpi, width, height), LabelSamples.Tube(stat: true), LabTubeTemplate.Draw));
            }
        }
    }

    [Fact]
    public void A_barcode_too_long_for_the_label_is_refused_rather_than_printed_unreadable() =>
        Should.Throw<LabelLayoutException>(() =>
                Draw(LabelSamples.Label(width: 38), LabelSamples.Tube() with { Barcode = "LAB-SAMPLE-ABCDEFGHIJKL" }, LabTubeTemplate.Draw))
            .Message.ShouldBe("The sample number is too long to print as a barcode on 38 mm wide labels. Use wider labels, or a shorter number format.");

    [Fact]
    public void A_label_too_short_for_a_readable_barcode_is_refused() =>
        Should.Throw<LabelLayoutException>(() => Draw(LabelSamples.Label(height: 15), LabelSamples.Tube(), LabTubeTemplate.Draw))
            .Message.ShouldContain("too short");

    [Fact]
    public void A_name_the_printer_fonts_cannot_print_is_refused() =>
        Should.Throw<LabelTextException>(() => Draw(LabelSamples.Label(), LabelSamples.Tube() with { PatientName = "रवि शर्मा" }, LabTubeTemplate.Draw));

    [Theory]
    [MemberData(nameof(Printers))]
    public void A_wristband_runs_along_the_band_with_the_allergy_mark_and_the_uhid_barcode(LabelLanguage language, int dpi)
    {
        var band = Draw(LabelSamples.Wristband(language, dpi), LabelSamples.Band(), (c, b) => WristbandTemplate.Draw(c, b, WristbandCode.Code128));

        BarcodesScan(band);
        var barcode = band.Elements.OfType<BarcodeElement>().ShouldHaveSingleItem();
        barcode.Rotation.ShouldBe(LabelRotation.Clockwise);
        barcode.Plan.Value.ShouldBe("NH00012345");
        band.Elements.OfType<TextElement>().ShouldAllBe(t => t.Rotation == LabelRotation.Clockwise);
        band.Elements.OfType<TextElement>().ShouldContain(t => t.Text == "Sharma Ravi Kumar");
        band.Elements.OfType<TextElement>().ShouldContain(t => t.Text == "UHID NH00012345  46Y/M");
        band.Elements.OfType<TextElement>().ShouldContain(t => t.Text == "IP2600123  Ward 3 / Bed 12");
        band.Elements.OfType<TextElement>().ShouldContain(t => t.Text == "ALLERGY");
        band.Elements.OfType<ReverseElement>().ShouldHaveSingleItem();

        // The name is the top line when the band is read: nearest the band's right-hand edge as printed.
        var lines = band.Elements.OfType<TextElement>().Where(t => t.Text != "ALLERGY").ToList();
        lines.MaxBy(t => t.X)!.Text.ShouldBe("Sharma Ravi Kumar");
    }

    [Theory]
    [MemberData(nameof(Printers))]
    public void A_wristband_can_carry_the_uhid_as_a_qr_code(LabelLanguage language, int dpi)
    {
        var band = Draw(LabelSamples.Wristband(language, dpi), LabelSamples.Band(WristbandAllergies.NoKnownAllergies), (c, b) => WristbandTemplate.Draw(c, b, WristbandCode.QrCode));

        var qr = band.Elements.OfType<QrElement>().ShouldHaveSingleItem();
        qr.Data.ShouldBe("NH00012345");
        (qr.Magnification * 25.4 / dpi).ShouldBeGreaterThanOrEqualTo(0.33, "modules big enough to scan");
        band.Elements.OfType<BarcodeElement>().ShouldBeEmpty();
        band.Elements.OfType<ReverseElement>().ShouldBeEmpty("No known allergies: no mark.");
        band.Elements.OfType<BoxElement>().ShouldBeEmpty();
        band.Elements.OfType<TextElement>().ShouldNotContain(t => t.Text.Contains("ALLERG"));
    }

    [Theory]
    [MemberData(nameof(Printers))]
    public void A_wristband_of_a_patient_whose_allergies_are_not_recorded_carries_an_outlined_mark(LabelLanguage language, int dpi)
    {
        var band = Draw(LabelSamples.Wristband(language, dpi), LabelSamples.Band(WristbandAllergies.NotRecorded),
            (c, b) => WristbandTemplate.Draw(c, b, WristbandCode.Code128));

        BarcodesScan(band);
        band.Elements.OfType<ReverseElement>().ShouldBeEmpty("Not the black ALLERGY mark.");
        band.Elements.OfType<TextElement>().ShouldNotContain(t => t.Text == "ALLERGY");
        var frame = band.Elements.OfType<BoxElement>().ShouldHaveSingleItem();
        (frame.Thickness * 2).ShouldBeLessThan(Math.Min(frame.Width, frame.Height), "A frame, not a solid block.");

        // Two lines inside the frame, "ALLERGIES" read first (nearer the band's right-hand edge as printed).
        var allergies = band.Elements.OfType<TextElement>().Single(t => t.Text == "ALLERGIES");
        var notRecorded = band.Elements.OfType<TextElement>().Single(t => t.Text == "NOT RECORDED");
        allergies.X.ShouldBeGreaterThan(notRecorded.X);
        foreach (var line in new[] { allergies, notRecorded })
        {
            line.X.ShouldBeGreaterThanOrEqualTo(frame.X + frame.Thickness);
            (line.X + line.Font.Height).ShouldBeLessThanOrEqualTo(frame.X + frame.Width - frame.Thickness);
            line.Y.ShouldBeGreaterThan(frame.Y + frame.Thickness);
        }

        // Between the patient's lines and the barcode, as the ALLERGY mark is.
        var barcode = band.Elements.OfType<BarcodeElement>().ShouldHaveSingleItem();
        (frame.Y + frame.Height).ShouldBeLessThan(barcode.Y);
    }

    [Theory]
    [InlineData(19, 150)]
    [InlineData(25, 180)]
    [InlineData(32, 280)]
    public void A_wristband_fits_infant_and_adult_bands(int width, int length)
    {
        foreach (var code in new[] { WristbandCode.Code128, WristbandCode.QrCode })
        {
            foreach (var allergies in Enum.GetValues<WristbandAllergies>())
            {
                BarcodesScan(Draw(LabelSamples.Wristband(width: width, length: length), LabelSamples.Band(allergies),
                    (c, b) => WristbandTemplate.Draw(c, b, code)));
            }
        }
    }

    [Fact]
    public void A_band_too_short_for_the_wristband_is_refused() =>
        Should.Throw<LabelLayoutException>(() =>
                Draw(LabelSamples.Wristband(length: 80), LabelSamples.Band(), (c, b) => WristbandTemplate.Draw(c, b, WristbandCode.Code128)))
            .Message.ShouldContain("too short");

    [Theory]
    [MemberData(nameof(Printers))]
    public void A_medicine_label_keeps_the_medicine_quantity_and_expiry_whole(LabelLanguage language, int dpi)
    {
        var label = Draw(LabelSamples.Label(language, dpi), LabelSamples.Medicine(), DispensingTemplate.Draw);
        var texts = label.Elements.OfType<TextElement>().Select(t => t.Text).ToList();

        string.Join(" ", texts).ShouldContain("10 tab");
        texts.ShouldContain("Exp Sep 2027");
        texts.ShouldContain("02 Oct 2026");
        texts.ShouldContain(t => t.StartsWith("1 tab - twice a day", StringComparison.Ordinal));
        label.Elements.OfType<BarcodeElement>().ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Printers))]
    public void A_blood_unit_label_writes_the_group_in_words_and_marks_units_not_for_issue(LabelLanguage language, int dpi)
    {
        var quarantined = Draw(LabelSamples.Label(language, dpi), LabelSamples.Unit(cleared: false), BloodUnitTemplate.Draw);
        var cleared = Draw(LabelSamples.Label(language, dpi), LabelSamples.Unit() with { BloodGroup = "O+" }, BloodUnitTemplate.Draw);

        BarcodesScan(quarantined);
        quarantined.Elements.OfType<TextElement>().Select(t => t.Text).ShouldContain("AB NEG");
        quarantined.Elements.OfType<TextElement>().Select(t => t.Text).ShouldContain("NOT FOR ISSUE");
        quarantined.Elements.OfType<TextElement>().Select(t => t.Text).ShouldContain("Exp 05 Nov 2026 14:00");
        quarantined.Elements.OfType<ReverseElement>().ShouldHaveSingleItem();
        cleared.Elements.OfType<TextElement>().Select(t => t.Text).ShouldContain("O POS");
        cleared.Elements.OfType<TextElement>().Select(t => t.Text).ShouldContain("NON-REACTIVE");
        cleared.Elements.OfType<ReverseElement>().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("O+", "O POS")]
    [InlineData("AB-", "AB NEG")]
    [InlineData("Bombay (hh)+", "Bombay (hh) POS")]
    [InlineData("Not grouped", "Not grouped")]
    public void Blood_groups_are_written_in_words(string group, string printed) => BloodUnitTemplate.GroupText(group).ShouldBe(printed);

    [Fact]
    public void A_long_blood_group_is_printed_smaller_never_shortened()
    {
        var label = Draw(LabelSamples.Label(), LabelSamples.Unit() with { BloodGroup = "Bombay (hh)−" }, BloodUnitTemplate.Draw);

        label.Elements.OfType<TextElement>().ShouldContain(t => t.Text == "Bombay (hh) NEG");
    }

    [Theory]
    [MemberData(nameof(Printers))]
    public void A_pack_label_says_when_the_pack_is_not_released(LabelLanguage language, int dpi)
    {
        var released = Draw(LabelSamples.Label(language, dpi), LabelSamples.Pack(), CssdPackTemplate.Draw);
        var waiting = Draw(LabelSamples.Label(language, dpi), LabelSamples.Pack(released: false), CssdPackTemplate.Draw);

        BarcodesScan(released);
        released.Elements.OfType<TextElement>().Select(t => t.Text).ShouldContain("Steam - Use by 30 Oct 2026");
        released.Elements.OfType<ReverseElement>().ShouldBeEmpty();
        waiting.Elements.OfType<TextElement>().Select(t => t.Text).ShouldContain("NOT RELEASED");
        waiting.Elements.OfType<ReverseElement>().ShouldHaveSingleItem();
    }

    [Theory]
    [MemberData(nameof(Printers))]
    public void An_asset_tag_carries_the_hospital_the_asset_and_its_tag(LabelLanguage language, int dpi)
    {
        var tag = Draw(LabelSamples.Label(language, dpi), LabelSamples.Asset(), AssetTagTemplate.Draw);

        BarcodesScan(tag);
        tag.Elements.OfType<BarcodeElement>().ShouldHaveSingleItem().Plan.Value.ShouldBe("AST-2600123");
        tag.Elements.OfType<TextElement>().Select(t => t.Text).ShouldBe(["Nana Hospitals", "Multipara monitor, ICU bed 4", "AST-2600123"]);
    }

    [Theory]
    [MemberData(nameof(Printers))]
    public void A_pharmacy_tag_carries_the_medicine_its_batch_and_the_barcode_the_counter_scans(LabelLanguage language, int dpi)
    {
        var tag = Draw(LabelSamples.Label(language, dpi), LabelSamples.PharmacyTag(), PharmacyTagTemplate.Draw);
        var item = Draw(LabelSamples.Label(language, dpi), LabelSamples.PharmacyTag(batch: false), PharmacyTagTemplate.Draw);

        BarcodesScan(tag);
        BarcodesScan(item);
        tag.Elements.OfType<BarcodeElement>().ShouldHaveSingleItem().Plan.Value.ShouldBe("PCM500#AB1234");
        tag.Elements.OfType<TextElement>().Select(t => t.Text)
            .ShouldBe(["Paracetamol 500 mg tab", "B AB1234   Exp Sep 2027", "MRP Rs 2.50/tab", "PCM500#AB1234"]);
        item.Elements.OfType<TextElement>().Select(t => t.Text).ShouldBe(["Paracetamol 500 mg tab", "PCM500"]);
    }

    [Fact]
    public void On_a_short_label_a_pharmacy_tag_gives_up_the_mrp_then_the_batch_line_but_keeps_its_barcode()
    {
        var lines = new Dictionary<int, string[]>();
        foreach (var height in new[] { 25, 15, 13 })
        {
            var tag = Draw(LabelSamples.Label(height: height), LabelSamples.PharmacyTag(), PharmacyTagTemplate.Draw);
            BarcodesScan(tag);
            tag.Elements.OfType<BarcodeElement>().ShouldHaveSingleItem();
            lines[height] = tag.Elements.OfType<TextElement>().Select(t => t.Text).ToArray();
        }

        var tiny = Should.Throw<LabelLayoutException>(() => Draw(LabelSamples.Label(height: 10), LabelSamples.PharmacyTag(), PharmacyTagTemplate.Draw));

        lines[25].ShouldBe(["Paracetamol 500 mg tab", "B AB1234   Exp Sep 2027", "MRP Rs 2.50/tab", "PCM500#AB1234"]);
        lines[15].ShouldBe(["Paracetamol 500 mg tab", "B AB1234   Exp Sep 2027", "PCM500#AB1234"]);
        lines[13].ShouldBe(["Paracetamol 500 mg tab", "PCM500#AB1234"]);
        tiny.Message.ShouldContain("too short for a pharmacy tag");
    }

    [Theory]
    [MemberData(nameof(Printers))]
    public void The_test_label_frames_the_label_and_shows_the_setup(LabelLanguage language, int dpi)
    {
        var label = Draw(LabelSamples.Label(language, dpi), LabelStock.Label, (c, s) => TestLabelTemplate.Draw(c, s, "OPD-RECEPTION-2", LabelSamples.Collected));
        var band = Draw(LabelSamples.Wristband(language, dpi), LabelStock.Wristband, (c, s) => TestLabelTemplate.Draw(c, s, "WARD-3", LabelSamples.Collected));

        BarcodesScan(label);
        BarcodesScan(band);
        var frame = label.Elements.OfType<BoxElement>().ShouldHaveSingleItem();
        frame.X.ShouldBe(label.Mm(1));
        (frame.X + frame.Width).ShouldBe(label.Width - label.Mm(1));
        label.Elements.OfType<TextElement>().Select(t => t.Text)
            .ShouldContain($"{(language == LabelLanguage.Zpl ? "ZPL" : "TSPL")}, {dpi} dpi, 50 x 25 mm");
        band.Elements.OfType<TextElement>().Select(t => t.Text).ShouldContain("HC360 test wristband");
    }
}
