namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The general-purpose label of <see cref="ILabelPrinterService"/>: one Code 128 barcode (its characters printed under
/// it) with up to six lines of text below, or a QR code with the lines beside it.
/// </summary>
internal static class GenericLabelTemplate
{
    public const int MaxTextLines = 6;
    public const int MaxLineLength = 80;

    public static void Draw(LabelCanvas c, LabelRequest request)
    {
        var m = c.Mm(2);
        var lines = request.TextLines
            .Select((line, index) => LabelText.Printable(line, $"Line {index + 1}"))
            .Where(line => line.Length > 0)
            .ToList();
        var inner = c.Width - (2 * m);

        if (request.Symbology == LabelSymbology.QrCode)
        {
            if (c.Language == LabelLanguage.Tspl && request.Value.Any(ch => ch is < ' ' or > '~' or '!'))
            {
                throw new LabelLayoutException("QR codes for TSPL printers hold printable ASCII without \"!\".");
            }

            var modules = QrSize.Modules(request.Value);
            var magnification = Math.Clamp((c.Height - (2 * m)) / modules, 1, 10);
            var qr = c.Qr(m, m, request.Value, modules, magnification);
            WriteLines(c, lines, m + qr.Size + m, m, c.Width - (m + qr.Size + m) - m, c.Height - (2 * m));
            return;
        }

        var plan = Code128Plan.For(request.Value);
        var module = c.ModuleWidth(plan.Modules, c.Width - 2, "The value");
        var small = c.Font(c.Mm(2.1)).Height;
        var barHeight = Math.Clamp((int)(c.Height * 0.4), c.Mm(4), Math.Max(c.Mm(4), c.Height - (2 * m) - small));
        c.Barcode((c.Width - (plan.Modules * module)) / 2, m, plan, module, barHeight);
        var y = m + barHeight + c.Mm(0.5);
        c.Text(m, y, small, c.Fit(plan.Value, small, inner));
        y += small + c.Mm(1);
        WriteLines(c, lines, m, y, inner, c.Height - m - y);
    }

    private static void WriteLines(LabelCanvas c, IReadOnlyList<string> lines, int x, int y, int width, int area)
    {
        if (lines.Count == 0 || width <= 0)
        {
            return;
        }

        // As large as the area allows, then smaller (down to 2 mm) until the longest line fits.
        var gap = c.Mm(0.3);
        var height = c.Font(Math.Clamp((area / lines.Count) - gap, c.Mm(2), c.Mm(4))).Height;
        while (height > c.Mm(2) && lines.Any(line => c.Measure(line, height) > width))
        {
            var smaller = c.Font(Math.Max(c.Mm(2), height - c.Mm(0.25))).Height;
            if (smaller >= height)
            {
                break;
            }

            height = smaller;
        }

        foreach (var line in lines)
        {
            if (y + height > c.Height - c.Mm(1))
            {
                break;
            }

            c.Text(x, y, height, c.Fit(line, height, width));
            y += height + gap;
        }
    }
}
