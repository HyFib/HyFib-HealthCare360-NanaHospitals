using System.Text;
using HyFib.HealthCare360.Integrations.Labels.Templates;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// The platform's <see cref="ILabelPrinterService"/> (INT-BAR): the general-purpose label. The server only writes
/// printer commands; HC360 Desktop sends them to the workstation's label printer (<c>printer.printRaw</c>). The typed
/// labels (sample tubes, wristbands, medicines, blood units, sterile packs, asset tags) are written by
/// <see cref="LabelRenderer"/> for the label endpoints.
/// </summary>
internal sealed class LabelPrinterService : ILabelPrinterService
{
    public static IReadOnlyList<string> Languages { get; } = ["zpl", "tspl"];

    /// <exception cref="ArgumentException">Unsupported language or an invalid request (the message is for staff).</exception>
    public string Build(LabelRequest request, string language)
    {
        var parsed = LabelMedia.ParseLanguage(language)
            ?? throw new ArgumentException($"Unsupported printer language '{language}'. Use ZPL or TSPL.", nameof(language));
        Validate(request);
        try
        {
            return LabelRenderer.Render(
                new LabelMedia(parsed, request.Dpi, request.WidthMm, request.HeightMm), [request], request.Copies, GenericLabelTemplate.Draw);
        }
        catch (Exception ex) when (ex is LabelLayoutException or LabelTextException)
        {
            throw new ArgumentException(ex.Message, nameof(request), ex);
        }
    }

    /// <summary>Limits the request validator cannot know about, with messages for staff.</summary>
    private static void Validate(LabelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Value))
        {
            throw new ArgumentException("The barcode value is required.", nameof(request));
        }

        if (request.Symbology == LabelSymbology.Code128 && !Code128Plan.IsEncodable(request.Value))
        {
            throw new ArgumentException(
                $"Code 128 values must be printable ASCII, at most {Code128Plan.MaxLength} characters, without ! > \" or \\.", nameof(request));
        }

        if (request.Symbology == LabelSymbology.QrCode && Encoding.UTF8.GetByteCount(request.Value) > QrSize.MaxBytes)
        {
            throw new ArgumentException($"QR values must be at most {QrSize.MaxBytes} bytes.", nameof(request));
        }

        if (request.WidthMm is < 10 or > 150 || request.HeightMm is < 10 or > 150)
        {
            throw new ArgumentException("Label width and height must be between 10 and 150 mm.", nameof(request));
        }

        if (request.Dpi is not (LabelMedia.LowResolution or LabelMedia.HighResolution))
        {
            throw new ArgumentException("The printer resolution must be 203 or 300 dpi.", nameof(request));
        }

        if (request.Copies is < 1 or > 100)
        {
            throw new ArgumentException("Copies must be between 1 and 100.", nameof(request));
        }

        if (request.TextLines.Count > GenericLabelTemplate.MaxTextLines || request.TextLines.Any(l => l.Length > GenericLabelTemplate.MaxLineLength))
        {
            throw new ArgumentException(
                $"At most {GenericLabelTemplate.MaxTextLines} text lines of {GenericLabelTemplate.MaxLineLength} characters each.", nameof(request));
        }
    }
}

/// <summary>Lays out each label on its own canvas with a template, and writes them all in the printer's language.</summary>
internal static class LabelRenderer
{
    /// <exception cref="LabelLayoutException">A label does not fit its media.</exception>
    /// <exception cref="LabelTextException">A label has text the printer's fonts cannot print.</exception>
    public static string Render<T>(LabelMedia media, IReadOnlyList<T> labels, int copies, Action<LabelCanvas, T> draw)
    {
        var canvases = new List<LabelCanvas>(labels.Count);
        foreach (var label in labels)
        {
            var canvas = new LabelCanvas(media);
            draw(canvas, label);
            canvases.Add(canvas);
        }

        return media.Language == LabelLanguage.Zpl ? ZplWriter.Write(canvases, copies) : TsplWriter.Write(canvases, copies);
    }
}
