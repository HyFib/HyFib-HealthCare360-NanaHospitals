using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// ZPL II for Zebra printers: one <c>^XA … ^XZ</c> format per label. <c>^CI28</c> selects UTF-8, and every field uses
/// <c>^FH</c> with <c>^</c>, <c>~</c>, <c>_</c> and non-ASCII bytes written as <c>_hh</c>, so label data can never become
/// a printer command. Text is font 0; Code 128 is <c>^BC</c> with no mode, its code sets given by invocation codes.
/// </summary>
internal static class ZplWriter
{
    public static string Write(IReadOnlyList<LabelCanvas> labels, int copies)
    {
        var zpl = new StringBuilder();
        foreach (var label in labels)
        {
            Line(zpl, "^XA");
            Line(zpl, "^CI28");
            Line(zpl, $"^PW{label.Width}");
            Line(zpl, $"^LL{label.Height}");
            Line(zpl, "^LH0,0");
            foreach (var element in label.Elements)
            {
                Line(zpl, Command(element));
            }

            Line(zpl, $"^PQ{copies}");
            Line(zpl, "^XZ");
        }

        return zpl.ToString();
    }

    private static string Command(LabelElement element) => element switch
    {
        TextElement t => $"^FO{t.X},{t.Y}^A0{Orientation(t.Rotation)},{t.Font.Height},{t.Font.Height}{Block(t)}^FH^FD{Escape(t.Text)}^FS",
        BarcodeElement b => $"^FO{b.X},{b.Y}^BY{b.ModuleWidth}^BC{Orientation(b.Rotation)},{b.Height},N,N,N,N^FH^FD{Escape(b.Plan.ZplData())}^FS",
        QrElement q => $"^FO{q.X},{q.Y}^BQN,2,{q.Magnification}^FH^FDQA,{Escape(q.Data)}^FS",
        BoxElement box => $"^FO{box.X},{box.Y}^GB{box.Width},{box.Height},{Math.Min(box.Thickness, Math.Min(box.Width, box.Height))}^FS",
        ReverseElement r => $"^FO{r.X},{r.Y}^FR^GB{r.Width},{r.Height},{Math.Min(r.Width, r.Height)}^FS",
        _ => throw new InvalidOperationException($"No ZPL for {element.GetType().Name}."),
    };

    private static string Orientation(LabelRotation rotation) => rotation == LabelRotation.Normal ? "N" : "R";

    /// <summary>A one-line field block that the printer aligns, for centred and end-aligned text.</summary>
    private static string Block(TextElement text) => text.Align switch
    {
        LabelAlign.Center => $"^FB{text.Box},1,0,C,0",
        LabelAlign.End => $"^FB{text.Box},1,0,R,0",
        _ => string.Empty,
    };

    /// <summary>
    /// Field data for <c>^FH</c> (indicator <c>_</c>): printable ASCII passes through; the command prefixes <c>^</c> and
    /// <c>~</c>, the indicator itself and every non-ASCII byte (UTF-8) become <c>_hh</c>; control characters are dropped.
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
