using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Export;

/// <summary>
/// Writes a report as a file in the format asked for: Excel and CSV in memory (<see cref="ReportExporter"/>), PDF from
/// the report's printable layout (<see cref="ReportPdf"/>) under the facility's letterhead, by the server's PDF
/// renderer. The export endpoint and scheduled e-mails both write through it, so a downloaded and an e-mailed file of
/// the same report are the same.
/// </summary>
internal sealed class ReportFileWriter(DocumentAssets assets, IPdfRenderer renderer)
{
    /// <exception cref="PdfRenderException">A PDF was asked for and could not be made (no browser on the server, or it failed or took too long).</exception>
    public async Task<ReportFile> WriteAsync(ReportDocument document, ExportFormat format, CancellationToken ct)
    {
        if (format != ExportFormat.Pdf)
        {
            return ReportExporter.Export(document, format);
        }

        var letterhead = await assets.LetterheadAsync(document.FacilityId, ct);
        return await PdfAsync(document, ReportPdf.Letterhead(letterhead, document), renderer, ct);
    }

    /// <summary>The PDF of the report under <paramref name="letterhead"/>, with the rows it lists and any notice.</summary>
    internal static async Task<ReportFile> PdfAsync(ReportDocument document, DocumentLetterhead letterhead, IPdfRenderer renderer, CancellationToken ct)
    {
        var pdf = ReportPdf.Build(document, letterhead);
        var content = await renderer.RenderAsync(pdf.ToPdfSource(), ct);
        return new ReportFile(
            pdf.FileName + ".pdf",
            ReportPdf.ContentType,
            content,
            Math.Min(document.Rows.Count, ReportPdf.MaxRows),
            ReportPdf.Notice(document.Rows.Count));
    }
}
