using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// TSPL/TSPL2 for TSC printers: <c>SIZE</c>, <c>GAP</c>, then <c>CLS … PRINT 1,copies</c> per label. Text uses the dot
/// fonts; Code 128 is <c>"128M"</c> with its code sets given by control codes; QR codes are model 2. Quotes in data are
/// written as TSPL's <c>\["]</c> and control characters are dropped, so label data can never end a command early.
/// TSPL rotates text and barcodes about their anchor point, so a clockwise element is anchored at its top-right corner.
/// </summary>
internal static class TsplWriter
{
    public static string Write(IReadOnlyList<LabelCanvas> labels, int copies)
    {
        var tspl = new StringBuilder();
        var media = labels[0].Media;
        Line(tspl, string.Create(CultureInfo.InvariantCulture, $"SIZE {media.WidthMm:0.##} mm,{media.HeightMm:0.##} mm"));
        Line(tspl, "GAP 2 mm,0 mm");
        Line(tspl, "DIRECTION 1");
        Line(tspl, "REFERENCE 0,0");
        foreach (var label in labels)
        {
            Line(tspl, "CLS");
            foreach (var element in label.Elements)
            {
                Line(tspl, Command(element));
            }

            Line(tspl, $"PRINT 1,{copies}");
        }

        return tspl.ToString();
    }

    private static string Command(LabelElement element) => element switch
    {
        TextElement t when t.Rotation == LabelRotation.Normal =>
            $"TEXT {Aligned(t.X, t)},{t.Y},\"{t.Font.Name}\",0,{t.Font.Multiplier},{t.Font.Multiplier},\"{Quote(t.Text)}\"",
        TextElement t =>
            $"TEXT {t.X + t.Font.Height},{Aligned(t.Y, t)},\"{t.Font.Name}\",90,{t.Font.Multiplier},{t.Font.Multiplier},\"{Quote(t.Text)}\"",
        BarcodeElement b when b.Rotation == LabelRotation.Normal =>
            $"BARCODE {b.X},{b.Y},\"128M\",{b.Height},0,0,{b.ModuleWidth},{b.ModuleWidth},\"{Quote(b.Plan.TsplData())}\"",
        BarcodeElement b =>
            $"BARCODE {b.X + b.Height},{b.Y},\"128M\",{b.Height},0,90,{b.ModuleWidth},{b.ModuleWidth},\"{Quote(b.Plan.TsplData())}\"",
        QrElement q => $"QRCODE {q.X},{q.Y},Q,{q.Magnification},A,0,M2,S7,\"{Quote(q.Data)}\"",
        BoxElement box when box.Thickness * 2 >= Math.Min(box.Width, box.Height) => $"BAR {box.X},{box.Y},{box.Width},{box.Height}",
        BoxElement box => $"BOX {box.X},{box.Y},{box.X + box.Width},{box.Y + box.Height},{box.Thickness}",
        ReverseElement r => $"REVERSE {r.X},{r.Y},{r.Width},{r.Height}",
        _ => throw new InvalidOperationException($"No TSPL for {element.GetType().Name}."),
    };

    /// <summary>Where aligned text starts: the dot fonts are fixed pitch, so the width is known exactly.</summary>
    private static int Aligned(int start, TextElement text)
    {
        var length = LabelFonts.Measure(LabelLanguage.Tspl, text.Text, text.Font.Height);
        return text.Align switch
        {
            LabelAlign.End => start + text.Box - length,
            LabelAlign.Center => start + ((text.Box - length) / 2),
            _ => start,
        };
    }

    /// <summary>
    /// A TSPL string: control characters dropped, the double quote as <c>\["]</c>, and the backslash, which starts that
    /// escape, as a slash.
    /// </summary>
    internal static string Quote(string value) =>
        new string(value.Where(c => !char.IsControl(c)).ToArray())
            .Replace('\\', '/')
            .Replace("\"", "\\[\"]", StringComparison.Ordinal);

    private static void Line(StringBuilder tspl, string command) => tspl.Append(command).Append("\r\n");
}
