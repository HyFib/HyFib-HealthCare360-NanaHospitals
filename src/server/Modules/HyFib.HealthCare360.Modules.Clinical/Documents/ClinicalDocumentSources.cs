using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>
/// The signed prescription as a PDF, for the "prescription shared" message: WhatsApp sends it in the template's document
/// header when the template has one, e-mail attaches it. It is made when the message goes out; a prescription cancelled
/// by then is not sent.
/// </summary>
internal sealed class PrescriptionDocumentSource(HospitalDbContext db, PrescriptionPrintBuilder builder, DocumentAssets assets, IPdfRenderer renderer)
    : INotificationDocumentSource
{
    public const string DocumentKind = "clinical.prescription";

    public string Kind => DocumentKind;

    public async Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken)
    {
        var prescription = await db.Set<Prescription>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.Id == id && p.Status == PrescriptionStatus.Signed, cancellationToken);
        if (prescription is null)
        {
            return null;
        }

        var model = await builder.BuildAsync(prescription, cancellationToken);
        return await DocumentPdf.CreateAsync(await PrescriptionDocument.BuildAsync(model, assets, cancellationToken), renderer, cancellationToken);
    }
}

/// <summary>
/// The signed (or amended) discharge summary as a PDF, for a message about the patient's discharge. A draft is never
/// sent. Nothing in this module queues such a message yet; the kind is here for the modules that tell patients about
/// their discharge.
/// </summary>
internal sealed class DischargeSummaryDocumentSource(DischargeSummaryService summaries, DocumentAssets assets, IPdfRenderer renderer)
    : INotificationDocumentSource
{
    public const string DocumentKind = "clinical.discharge-summary";

    public string Kind => DocumentKind;

    public async Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken)
    {
        var print = await summaries.PrintAsync(id, cancellationToken);
        if (print.IsFailure || print.Value.Summary.Status == DischargeSummaryStatus.Draft)
        {
            return null;
        }

        return await DocumentPdf.CreateAsync(await DischargeSummaryDocument.BuildAsync(print.Value, assets, cancellationToken), renderer, cancellationToken);
    }
}

file static class DocumentPdf
{
    public static async Task<NotificationFile> CreateAsync(PrintableDocument document, IPdfRenderer renderer, CancellationToken cancellationToken) =>
        new($"{document.FileName}.pdf", await renderer.RenderAsync(document.ToPdfSource(), cancellationToken), "application/pdf");
}
