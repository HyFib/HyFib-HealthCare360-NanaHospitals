using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>
/// A document for the staff app to print as it is (HTML with its page settings). The same HTML makes the document's PDF.
/// </summary>
/// <param name="PageSize">"A4" or "A5", as the desktop printer bridge expects.</param>
public sealed record PrintableDocumentDto(string Title, string FileName, string PageSize, bool Landscape, string Html);

/// <summary>How document endpoints answer: the printable HTML, or the PDF.</summary>
public static class DocumentResults
{
    public static PrintableDocumentDto ToDto(this PrintableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PrintableDocumentDto(document.Title, document.FileName, document.PageSize.ToString(), document.Landscape, document.ToHtml());
    }

    /// <summary>
    /// The document as a PDF download (<c>application/pdf</c>, attachment). When PDFs can't be made right now the answer
    /// is 503 with the reason, so the screen can say so and still offer to print.
    /// </summary>
    public static async Task<IResult> ToPdfAsync(this PrintableDocument document, IPdfRenderer renderer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderer);
        try
        {
            var pdf = await renderer.RenderAsync(document.ToPdfSource(), cancellationToken);
            return TypedResults.File(pdf, "application/pdf", document.FileName + ".pdf");
        }
        catch (PdfRenderException ex)
        {
            return PdfUnavailable(ex.Message);
        }
    }

    /// <summary>The answer when a PDF can't be made right now: 503 with code <c>pdf.unavailable</c> and the reason.</summary>
    public static IResult PdfUnavailable(string detail) => TypedResults.Problem(
        detail: detail,
        statusCode: StatusCodes.Status503ServiceUnavailable,
        title: "The PDF could not be made",
        extensions: new Dictionary<string, object?> { ["code"] = "pdf.unavailable" });
}
