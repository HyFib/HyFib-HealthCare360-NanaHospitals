namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>The printer languages HC360 writes for label printers.</summary>
public enum LabelLanguage
{
    /// <summary>ZPL II: Zebra printers, and printers that emulate ZPL.</summary>
    Zpl,

    /// <summary>TSPL/TSPL2: TSC printers, and printers that speak TSPL.</summary>
    Tspl,
}

/// <summary>What a label is printed on: a roll of labels, or wristband bands.</summary>
public enum LabelStock
{
    Label = 0,
    Wristband,
}

/// <summary>
/// What one label is printed on: the printer's language and resolution, and the size of one label (or one wristband
/// band) in millimetres. Positions on the label are printer dots: 8 per millimetre at 203 dpi, about 11.8 at 300 dpi.
/// </summary>
internal sealed record LabelMedia(LabelLanguage Language, int Dpi, decimal WidthMm, decimal HeightMm)
{
    public const int LowResolution = 203;
    public const int HighResolution = 300;

    /// <summary>Label width (across the print head), in dots.</summary>
    public int Width => Dots(WidthMm);

    /// <summary>Label length (along the feed), in dots.</summary>
    public int Height => Dots(HeightMm);

    public int Dots(decimal millimetres) => (int)Math.Round(millimetres * Dpi / 25.4m, MidpointRounding.AwayFromZero);

    public int Dots(double millimetres) => (int)Math.Round(millimetres * Dpi / 25.4, MidpointRounding.AwayFromZero);

    /// <summary>"zpl", "tspl" or "tspl2" (any case); null for anything else.</summary>
    public static LabelLanguage? ParseLanguage(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "zpl" => LabelLanguage.Zpl,
        "tspl" or "tspl2" => LabelLanguage.Tspl,
        _ => null,
    };

    public static string Name(LabelLanguage language) => language == LabelLanguage.Zpl ? "zpl" : "tspl";
}

/// <summary>A label that cannot be printed as asked (too small for its barcode, say). The message is for staff.</summary>
internal sealed class LabelLayoutException(string message) : Exception(message);

/// <summary>Label text in letters the printer's own fonts do not have (Devanagari, say). The message is for staff.</summary>
internal sealed class LabelTextException(string message) : Exception(message);
