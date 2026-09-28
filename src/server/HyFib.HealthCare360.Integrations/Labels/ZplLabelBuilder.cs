using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// ZPL II for Zebra printers: <c>^BC</c> Code 128 with the human-readable line, or <c>^BQ</c> QR (model 2, level Q)
/// with text beside it; <c>^CI28</c> selects UTF-8 and every field uses <c>^FH</c> so data is hex-escaped and can never
/// inject printer commands.
/// </summary>
internal static class ZplLabelBuilder
{
    public static string Build(LabelRequest request)
    {
        LabelLayout.Validate(request);
        var width = LabelLayout.Dots(request.WidthMm);
        var height = LabelLayout.Dots(request.HeightMm);
        var margin = LabelLayout.Margin;
        var lines = LabelLayout.CleanLines(request).ToList();
        var zpl = new StringBuilder();
        Line(zpl, "^XA");
        Line(zpl, "^CI28");
        Line(zpl, $"^PW{width}");
        Line(zpl, $"^LL{height}");
        Line(zpl, "^LH0,0");

        int textX, textY, textArea;
        if (request.Symbology == LabelSymbology.Code128)
        {
            var moduleWidth = LabelLayout.Code128ModuleWidth(request.Value, width);
            var barHeight = Math.Clamp((int)(height * 0.4), 30, Math.Max(30, height - (2 * margin) - 24));
            Line(zpl, $"^FO{margin},{margin}^BY{moduleWidth},3,{barHeight}^BCN,{barHeight},Y,N,N,A^FH^FD{Escape(request.Value)}^FS");
            textX = margin;
            textY = margin + barHeight + 28; // below the interpretation line
            textArea = height - textY - margin;
        }
        else
        {
            var modules = LabelLayout.QrModules(request.Value);
            var magnification = Math.Clamp((height - (2 * margin)) / modules, 1, 10);
            Line(zpl, $"^FO{margin},{margin}^BQN,2,{magnification}^FH^FDQA,{Escape(request.Value)}^FS");
            textX = margin + (modules * magnification) + margin;
            textY = margin;
            textArea = height - (2 * margin);
        }

        if (lines.Count > 0)
        {
            var fontHeight = Math.Clamp(textArea / lines.Count, 16, 32);
            foreach (var line in lines)
            {
                Line(zpl, $"^FO{textX},{textY}^A0N,{fontHeight},{fontHeight}^FH^FD{Escape(line)}^FS");
                textY += fontHeight + 2;
            }
        }

        Line(zpl, $"^PQ{request.Copies}");
        Line(zpl, "^XZ");
        return zpl.ToString();
    }

    /// <summary>
    /// Field data for <c>^FH</c> (indicator <c>_</c>): printable ASCII passes through; the command prefixes
    /// <c>^</c> and <c>~</c>, the indicator itself and every non-ASCII byte (UTF-8) become <c>_hh</c>.
    /// </summary>
    internal static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        Span<byte> bytes = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is >= ' ' and <= '~' and not '^' and not '~' and not '_')
            {
                builder.Append((char)rune.Value);
                continue;
            }

            if (Rune.IsControl(rune))
            {
                continue;
            }

            var count = rune.EncodeToUtf8(bytes);
            foreach (var b in bytes[..count])
            {
                builder.Append('_').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static void Line(StringBuilder zpl, string command) => zpl.Append(command).Append('\n');
}
