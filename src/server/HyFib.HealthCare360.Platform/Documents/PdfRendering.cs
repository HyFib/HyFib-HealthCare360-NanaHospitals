namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>
/// A complete HTML page to turn into a PDF. It is rendered with scripts switched off and no network access, so
/// everything it shows must be inside it: styles inline, images as <c>data:</c> URIs or inline SVG. The page size,
/// margins and running footers come from the page's own CSS (<c>@page</c> and its margin boxes), so the PDF matches a
/// copy printed from the browser.
/// </summary>
/// <param name="Html">The whole document, from <c>&lt;!doctype html&gt;</c> on.</param>
public sealed record PdfSource(string Html);

/// <summary>Whether this server can make PDFs, and if not, why (for the administrator dashboard).</summary>
public sealed record PdfRendererStatus(bool Available, string? Browser, string? Problem);

/// <summary>Turns HTML documents into PDFs on the server, for documents that are downloaded, e-mailed or sent on WhatsApp.</summary>
public interface IPdfRenderer
{
    PdfRendererStatus Status { get; }

    /// <exception cref="PdfRenderException">The PDF could not be made: no browser on this server, or it failed or took too long.</exception>
    Task<byte[]> RenderAsync(PdfSource source, CancellationToken cancellationToken);
}

/// <summary>A PDF could not be made. The message is safe to show to staff.</summary>
public sealed class PdfRenderException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Used when the Integrations project is not loaded (module tests): every PDF fails with a clear message.</summary>
internal sealed class UnavailablePdfRenderer : IPdfRenderer
{
    public PdfRendererStatus Status { get; } = new(false, null, "PDF generation is not set up on this server.");

    public Task<byte[]> RenderAsync(PdfSource source, CancellationToken cancellationToken) =>
        throw new PdfRenderException(Status.Problem!);
}
