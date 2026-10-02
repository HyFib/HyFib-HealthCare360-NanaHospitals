using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Diagnostics.Documents;

/// <summary>
/// The released lab report as a PDF, for the "report ready" message (LIS-07): attached to the e-mail and sent in the
/// WhatsApp template's document header. It is made when the message goes out, so it shows the report as it is then.
/// </summary>
internal sealed class LabReportDocumentSource(LabReportBuilder builder, ReportDocumentFrame frame, IPdfRenderer renderer) : INotificationDocumentSource
{
    public const string DocumentKind = "diagnostics.lab-report";

    public string Kind => DocumentKind;

    public async Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken)
    {
        var view = await builder.BuildAsync(id, releasedOnly: true, cancellationToken);
        if (view is null || view.Sections.Count == 0)
        {
            return null;
        }

        var document = await LabReportDocument.BuildAsync(view, releasedOnly: true, frame, cancellationToken);
        return await PdfAsync(document, renderer, cancellationToken);
    }

    internal static async Task<NotificationFile> PdfAsync(PrintableDocument document, IPdfRenderer renderer, CancellationToken cancellationToken) =>
        new($"{document.FileName}.pdf", await renderer.RenderAsync(document.ToPdfSource(), cancellationToken), "application/pdf");
}

/// <summary>
/// The released radiology report as a PDF, for the "report ready" message (IMG-03). The id is the imaging study's, as
/// everywhere in radiology. A report that is not released (or no longer exists) gives no document.
/// </summary>
internal sealed class RadiologyReportDocumentSource(RadiologyReportService reports, ReportDocumentFrame frame, IPdfRenderer renderer) : INotificationDocumentSource
{
    public const string DocumentKind = "diagnostics.radiology-report";

    public string Kind => DocumentKind;

    public async Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken)
    {
        var view = await reports.ViewAsync(id, releasedOnly: true, cancellationToken);
        if (view is null)
        {
            return null;
        }

        var document = await RadiologyReportDocument.BuildAsync(view, frame, cancellationToken);
        return await LabReportDocumentSource.PdfAsync(document, renderer, cancellationToken);
    }
}

/// <summary>
/// The released cardiology report as a PDF, for the "report ready" message (CAR-04). The id is the cardiology study's.
/// A report that is not released (or no longer exists) gives no document.
/// </summary>
internal sealed class CardiologyReportDocumentSource(CardiologyService cardiology, ReportDocumentFrame frame, IPdfRenderer renderer) : INotificationDocumentSource
{
    public const string DocumentKind = "diagnostics.cardiology-report";

    public string Kind => DocumentKind;

    public async Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken)
    {
        var view = await cardiology.ViewAsync(id, releasedOnly: true, cancellationToken);
        if (view is null)
        {
            return null;
        }

        var document = await CardiologyReportDocument.BuildAsync(view, frame, cancellationToken);
        return await LabReportDocumentSource.PdfAsync(document, renderer, cancellationToken);
    }
}
