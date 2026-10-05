namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>Normal text runs left to right; <see cref="Clockwise"/> runs down the label, letter tops facing right.</summary>
internal enum LabelRotation
{
    Normal,
    Clockwise,
}

internal enum LabelAlign
{
    Start,
    Center,
    End,
}

/// <summary>
/// Something drawn on a label. Every element is placed by the top-left corner of the area it covers, in dots of the
/// unrotated label (x across the print head, y along the feed), whatever its rotation; the writers turn that into each
/// language's own anchor.
/// </summary>
internal abstract record LabelElement;

/// <param name="Box">For centred or end-aligned text: the length of the line it is aligned in (along its direction).</param>
internal sealed record TextElement(int X, int Y, LabelFont Font, string Text, LabelRotation Rotation, LabelAlign Align, int Box) : LabelElement;

/// <param name="ModuleWidth">Dots per module, a whole number so every bar prints at the same width.</param>
/// <param name="Height">Bar height in dots (across the label for a rotated barcode).</param>
internal sealed record BarcodeElement(int X, int Y, Code128Plan Plan, int ModuleWidth, int Height, LabelRotation Rotation) : LabelElement
{
    public int Length => Plan.Modules * ModuleWidth;
}

/// <param name="Magnification">Dots per QR module.</param>
internal sealed record QrElement(int X, int Y, string Data, int Modules, int Magnification) : LabelElement
{
    public int Size => Modules * Magnification;
}

/// <summary>A frame, or a solid block when the thickness fills it.</summary>
internal sealed record BoxElement(int X, int Y, int Width, int Height, int Thickness) : LabelElement;

/// <summary>Swaps black and white in the area: white text on black, drawn after the text it covers.</summary>
internal sealed record ReverseElement(int X, int Y, int Width, int Height) : LabelElement;

/// <summary>
/// One label being laid out: its media, what is on it so far, and the measuring and placing helpers the templates use.
/// Positions are checked against the label, so a template mistake fails a test instead of printing off the edge.
/// </summary>
internal sealed class LabelCanvas(LabelMedia media)
{
    private readonly List<LabelElement> _elements = [];

    public LabelMedia Media { get; } = media;

    public LabelLanguage Language => Media.Language;

    public int Width => Media.Width;

    public int Height => Media.Height;

    public IReadOnlyList<LabelElement> Elements => _elements;

    public int Mm(double millimetres) => Media.Dots(millimetres);

    public LabelFont Font(int height) => LabelFonts.Pick(Language, height);

    public int Measure(string text, int height) => LabelFonts.Measure(Language, text, height);

    public string Fit(string text, int height, int width) => LabelFonts.Fit(Language, text, height, width);

    public IReadOnlyList<string> Wrap(string text, int height, int width, int maxLines) =>
        LabelFonts.Wrap(Language, text, height, width, maxLines);

    /// <summary>Text across the label. Returns the dots it takes along its line (0 for empty text).</summary>
    public int Text(int x, int y, int height, string text, LabelAlign align = LabelAlign.Start, int box = 0)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var font = Font(height);
        var width = Measure(text, height);
        var start = align switch
        {
            LabelAlign.End => x + box - width,
            LabelAlign.Center => x + ((box - width) / 2),
            _ => x,
        };
        Check(Math.Min(x, start), y, Math.Max(width, box), font.Height, "text");
        _elements.Add(new TextElement(x, y, font, text, LabelRotation.Normal, align, box));
        return width;
    }

    /// <summary>Text running down the label (letter tops facing right). x is the left edge of the line's height.</summary>
    public int TextDown(int x, int y, int height, string text, LabelAlign align = LabelAlign.Start, int box = 0)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        var font = Font(height);
        var length = Measure(text, height);
        Check(x, y, font.Height, Math.Max(length, box), "text");
        _elements.Add(new TextElement(x, y, font, text, LabelRotation.Clockwise, align, box));
        return length;
    }

    public BarcodeElement Barcode(int x, int y, Code128Plan plan, int moduleWidth, int height, LabelRotation rotation = LabelRotation.Normal)
    {
        var barcode = new BarcodeElement(x, y, plan, moduleWidth, height, rotation);
        if (rotation == LabelRotation.Normal)
        {
            Check(x, y, barcode.Length, height, "barcode");
        }
        else
        {
            Check(x, y, height, barcode.Length, "barcode");
        }

        _elements.Add(barcode);
        return barcode;
    }

    public QrElement Qr(int x, int y, string data, int modules, int magnification)
    {
        var qr = new QrElement(x, y, data, modules, magnification);
        Check(x, y, qr.Size, qr.Size, "QR code");
        _elements.Add(qr);
        return qr;
    }

    public void Box(int x, int y, int width, int height, int thickness)
    {
        Check(x, y, width, height, "frame");
        _elements.Add(new BoxElement(x, y, width, height, thickness));
    }

    /// <summary>White on black for the area: call after drawing the text inside it.</summary>
    public void Reverse(int x, int y, int width, int height)
    {
        Check(x, y, width, height, "highlight");
        _elements.Add(new ReverseElement(x, y, width, height));
    }

    /// <summary>
    /// The widest module, in whole dots, that lets a barcode of <paramref name="modules"/> modules and its quiet zones
    /// (ten modules each side) fit in <paramref name="span"/> dots, no narrower than 0.19 mm (two dots at 203 dpi, three
    /// at 300) so scanners read it, and no wider than 0.5 mm.
    /// </summary>
    /// <exception cref="LabelLayoutException">Even the narrowest readable module does not fit.</exception>
    public int ModuleWidth(int modules, int span, string what)
    {
        var narrowest = (int)Math.Ceiling(0.19 * Media.Dpi / 25.4);
        var widest = Math.Max(narrowest, (int)Math.Floor(0.5 * Media.Dpi / 25.4));
        var fits = span / (modules + 20);
        if (fits < narrowest)
        {
            throw new LabelLayoutException(
                $"{what} is too long to print as a barcode on {Media.WidthMm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} mm "
                + "wide labels. Use wider labels, or a shorter number format.");
        }

        return Math.Min(fits, widest);
    }

    private void Check(int x, int y, int width, int height, string what)
    {
        if (x < 0 || y < 0 || width < 0 || height < 0 || x + width > Width || y + height > Height)
        {
            throw new LabelLayoutException($"The {what} does not fit on a {Describe(Media)} label.");
        }
    }

    public static string Describe(LabelMedia media) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{media.WidthMm:0.#} × {media.HeightMm:0.#} mm");
}
