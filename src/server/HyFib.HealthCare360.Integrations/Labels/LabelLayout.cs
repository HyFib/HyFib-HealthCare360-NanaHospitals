using System.Text;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// Geometry shared by the ZPL and TSPL builders for 203 dpi thermal printers (8 dots per millimetre), and request
/// validation with staff-facing messages (thrown as <see cref="ArgumentException"/>).
/// </summary>
internal static class LabelLayout
{
    public const int Dpi = 203;
    public const int MaxTextLines = 6;
    public const int MaxLineLength = 80;
    public const int MaxCode128Length = 48;
    public const int MaxQrLength = 400;

    /// <summary>Byte capacity of QR versions 1–20 at error-correction level Q (ISO/IEC 18004, byte mode).</summary>
    private static readonly int[] QrCapacityQ = [11, 20, 32, 46, 60, 74, 86, 108, 130, 151, 177, 203, 241, 258, 292, 322, 364, 394, 442, 482];

    public static int Dots(double millimetres) => (int)Math.Round(millimetres * Dpi / 25.4, MidpointRounding.AwayFromZero);

    public static int Margin => Dots(2);

    public static void Validate(LabelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            throw new ArgumentException("The barcode value is required.", nameof(request));
        }

        if (request.Symbology == LabelSymbology.Code128
            && (request.Value.Length > MaxCode128Length || request.Value.Any(c => c is < ' ' or > '~')))
        {
            throw new ArgumentException($"Code 128 values must be printable ASCII and at most {MaxCode128Length} characters.", nameof(request));
        }

        if (request.Symbology == LabelSymbology.QrCode && Encoding.UTF8.GetByteCount(request.Value) > MaxQrLength)
        {
            throw new ArgumentException($"QR values must be at most {MaxQrLength} bytes.", nameof(request));
        }

        if (request.WidthMm is < 10 or > 150 || request.HeightMm is < 10 or > 150)
        {
            throw new ArgumentException("Label width and height must be between 10 and 150 mm.", nameof(request));
        }

        if (request.Copies is < 1 or > 100)
        {
            throw new ArgumentException("Copies must be between 1 and 100.", nameof(request));
        }

        if (request.TextLines.Count > MaxTextLines || request.TextLines.Any(l => l.Length > MaxLineLength))
        {
            throw new ArgumentException($"At most {MaxTextLines} text lines of {MaxLineLength} characters each.", nameof(request));
        }
    }

    /// <summary>Upper bound of Code 128 modules: start + 11 per character + check character + stop (13).</summary>
    public static int Code128Modules(string value) => (11 * (value.Length + 2)) + 13;

    /// <summary>Narrowest bar width (dots) that fits the label; throws when even one dot per module is too wide.</summary>
    public static int Code128ModuleWidth(string value, int widthDots)
    {
        var available = widthDots - (2 * Margin);
        var modules = Code128Modules(value);
        if (modules > available)
        {
            throw new ArgumentException("The barcode value is too long for this label width.", nameof(value));
        }

        return Math.Clamp(available / modules, 1, 3);
    }

    /// <summary>Modules per side of the smallest QR version (level Q) that holds the value.</summary>
    public static int QrModules(string value)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        var version = Array.FindIndex(QrCapacityQ, capacity => capacity >= bytes) + 1;
        return 17 + (4 * (version == 0 ? QrCapacityQ.Length : version));
    }

    /// <summary>Text lines without control characters (they would end or corrupt the printer command).</summary>
    public static IEnumerable<string> CleanLines(LabelRequest request) =>
        request.TextLines.Select(line => new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim()).Where(line => line.Length > 0);
}
