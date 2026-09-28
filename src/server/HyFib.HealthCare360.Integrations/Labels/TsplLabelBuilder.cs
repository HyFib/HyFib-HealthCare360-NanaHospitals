using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// TSPL/TSPL2 for TSC-compatible printers: <c>SIZE</c>/<c>GAP</c>/<c>CLS</c>, <c>BARCODE … "128"</c> or
/// <c>QRCODE</c>, <c>TEXT</c> lines and <c>PRINT 1,copies</c>. Quotes in data are written as TSPL's <c>\["]</c>.
/// </summary>
internal static class TsplLabelBuilder
{
    public static string Build(LabelRequest request)
    {
        LabelLayout.Validate(request);
        var width = LabelLayout.Dots(request.WidthMm);
        var height = LabelLayout.Dots(request.HeightMm);
        var margin = LabelLayout.Margin;
        var lines = LabelLayout.CleanLines(request).ToList();
        var tspl = new StringBuilder();
        Line(tspl, string.Create(CultureInfo.InvariantCulture, $"SIZE {request.WidthMm} mm,{request.HeightMm} mm"));
        Line(tspl, "GAP 2 mm,0 mm");
        Line(tspl, "DIRECTION 1");
        Line(tspl, "REFERENCE 0,0");
        Line(tspl, "CLS");

        int textX, textY, textArea;
        if (request.Symbology == LabelSymbology.Code128)
        {
            var narrow = LabelLayout.Code128ModuleWidth(request.Value, width);
            var barHeight = Math.Clamp((int)(height * 0.4), 30, Math.Max(30, height - (2 * margin) - 24));
            Line(tspl, $"BARCODE {margin},{margin},\"128\",{barHeight},1,0,{narrow},{narrow},\"{Quote(request.Value)}\"");
            textX = margin;
            textY = margin + barHeight + 28;
            textArea = height - textY - margin;
        }
        else
        {
            var modules = LabelLayout.QrModules(request.Value);
            var cell = Math.Clamp((height - (2 * margin)) / modules, 1, 10);
            Line(tspl, $"QRCODE {margin},{margin},Q,{cell},A,0,\"{Quote(request.Value)}\"");
            textX = margin + (modules * cell) + margin;
            textY = margin;
            textArea = height - (2 * margin);
        }

        if (lines.Count > 0)
        {
            // Built-in fonts: "2" is 12×20 dots, "3" is 16×24 dots.
            var lineHeight = textArea / lines.Count;
            var (font, fontHeight) = lineHeight >= 28 ? ("3", 24) : ("2", 20);
            foreach (var line in lines)
            {
                Line(tspl, $"TEXT {textX},{textY},\"{font}\",0,1,1,\"{Quote(line)}\"");
                textY += fontHeight + 4;
            }
        }

        Line(tspl, $"PRINT 1,{request.Copies}");
        return tspl.ToString();
    }

    internal static string Quote(string value) =>
        new string(value.Where(c => !char.IsControl(c)).ToArray()).Replace("\"", "\\[\"]", StringComparison.Ordinal);

    private static void Line(StringBuilder tspl, string command) => tspl.Append(command).Append("\r\n");
}
