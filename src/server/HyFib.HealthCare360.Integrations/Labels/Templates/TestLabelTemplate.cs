namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The label "Printers on this computer" prints to check a printer: a frame 1 mm inside the edges (if a side is cut
/// off, the size or the printer's position setting is wrong), what HC360 thinks the printer is, and a sample barcode
/// at the module width real labels get, to try with the scanner.
/// <code>
/// +--------------------------------------+
/// | HC360 test label                     |
/// | ZPL, 203 dpi, 50 x 25 mm             |
/// | ||||||||| HC360-TEST ||||||||||      |
/// | OPD-RECEPTION-2         02 Oct 10:30 |
/// +--------------------------------------+
/// </code>
/// On wristband stock the same lines run along the band.
/// </summary>
internal static class TestLabelTemplate
{
    public const string SampleCode = "HC360-TEST";

    public static void Draw(LabelCanvas c, LabelStock stock, string? computer, DateTimeOffset now)
    {
        var where = LabelText.Printable(computer, "The computer's name");
        var spec = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{(c.Language == LabelLanguage.Zpl ? "ZPL" : "TSPL")}, {c.Media.Dpi} dpi, {c.Media.WidthMm:0.#} x {c.Media.HeightMm:0.#} mm");
        var footer = LabelFormat.Join("  ", where, LabelFormat.DayTime(now));
        var plan = Code128Plan.For(SampleCode);
        var edge = c.Mm(1);
        var line = Math.Max(2, c.Mm(0.25));
        c.Box(edge, edge, c.Width - (2 * edge), c.Height - (2 * edge), line);

        if (stock == LabelStock.Wristband)
        {
            DrawBand(c, spec, footer, plan, edge + line);
            return;
        }

        var s = LabelFormat.Scale(c.Media);
        var m = edge + line + c.Mm(1);
        var inner = c.Width - (2 * m);
        var big = c.Font(c.Mm(2.8 * s)).Height;
        var small = c.Font(c.Mm(2.1 * s)).Height;
        var gap = c.Mm(0.5 * s);

        var y = m;
        c.Text(m, y, big, c.Fit("HC360 test label", big, inner));
        y += big + gap;
        c.Text(m, y, small, c.Fit(spec, small, inner));
        y += small + gap;

        var barHeight = Math.Min(c.Height - m - small - gap - y, c.Mm(10));
        if (barHeight < c.Mm(3))
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too small for the test label.");
        }

        var module = c.ModuleWidth(plan.Modules, c.Width - (2 * (edge + line)), "The test code");
        c.Barcode((c.Width - (plan.Modules * module)) / 2, y, plan, module, barHeight);
        y += barHeight + gap;
        var timeWidth = c.Text(m, y, small, LabelFormat.DayTime(now), LabelAlign.End, inner) + c.Mm(1);
        c.Text(m, y, small, c.Fit(where, small, inner - timeWidth));
    }

    private static void DrawBand(LabelCanvas c, string spec, string footer, Code128Plan plan, int inside)
    {
        var s = Math.Clamp((double)c.Media.WidthMm / 25, 0.75, 1.3);
        var m = inside + c.Mm(1);
        var across = c.Width - (2 * m);
        var big = c.Font(c.Mm(3.2 * s)).Height;
        var small = c.Font(c.Mm(2.4 * s)).Height;
        var lineGap = c.Mm(0.8 * s);
        var module = c.ModuleWidth(plan.Modules, Math.Min(c.Mm(60), c.Height / 3), "The test code");
        var codeLength = (plan.Modules + 20) * module;
        var textLength = Math.Min(c.Height - (2 * m) - codeLength - c.Mm(4), c.Mm(100));
        if (textLength < c.Mm(30))
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} band is too short for the test wristband.");
        }

        var x = c.Width - m;
        var used = 0;
        foreach (var (height, text) in new[] { (big, "HC360 test wristband"), (small, spec), (small, footer) })
        {
            x -= height;
            if (x < m || text.Length == 0)
            {
                break;
            }

            used = Math.Max(used, c.TextDown(x, m, height, c.Fit(text, height, textLength)));
            x -= lineGap;
        }

        var barHeight = across - c.Mm(2);
        c.Barcode(m + ((across - barHeight) / 2), m + used + c.Mm(4) + (10 * module), plan, module, barHeight, LabelRotation.Clockwise);
    }
}
