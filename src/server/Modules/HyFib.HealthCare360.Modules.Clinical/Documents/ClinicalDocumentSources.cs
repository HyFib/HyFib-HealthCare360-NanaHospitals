using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>
/// The signed prescription as a PDF, for the "prescription shared" message: WhatsApp sends it in the template's document
/// header when the template has one, e-mail attaches it. It is made when the message goes out, with the DSC signature as
/// it checks out then. There is none for a prescription cancelled by then, nor for one the DSC rules do not let the
/// hospital issue (<see cref="PrescriptionFinalCopies"/>): the message then goes without it, or not at all on a WhatsApp
/// template with a document header. Where the DSC is required the message is queued only once the doctor has signed
/// with it, so that happens only when the setting or the trust folder changed in between.
/// </summary>
internal sealed class PrescriptionDocumentSource(
    HospitalDbContext db,
    PrescriptionPrintBuilder builder,
    PrescriptionFinalCopies finalCopies,
    DocumentAssets assets,
    IPdfRenderer renderer)
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

        var signature = await finalCopies.CheckAsync(prescription, cancellationToken);
        if (PrescriptionFinalCopies.Problem(signature) is not null)
        {
            return null;
        }

        var model = await builder.BuildAsync(prescription, signature, cancellationToken);
        return await DocumentPdf.CreateAsync(await PrescriptionDocument.BuildAsync(model, assets, cancellationToken), renderer, cancellationToken);
    }
}

/// <summary>
/// The signed (or amended) discharge summary as a PDF, for a message about the patient's discharge. A draft is never
/// sent. Its headings are in the language the patient prefers (PP-06). Nothing in this module queues such a message
/// yet; the kind is here for the modules that tell patients about their discharge.
/// </summary>
internal sealed class DischargeSummaryDocumentSource(
    DischargeSummaryService summaries, DocumentAssets assets, IPdfRenderer renderer, IPatientLanguageSource languages)
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

        var text = DocumentText.For(await languages.LanguageOfAsync(print.Value.Patient.Id, cancellationToken));
        return await DocumentPdf.CreateAsync(await DischargeSummaryDocument.BuildAsync(print.Value, assets, text, cancellationToken), renderer, cancellationToken);
    }
}

file static class DocumentPdf
{
    public static async Task<NotificationFile> CreateAsync(PrintableDocument document, IPdfRenderer renderer, CancellationToken cancellationToken) =>
        new($"{document.FileName}.pdf", await renderer.RenderAsync(document.ToPdfSource(), cancellationToken), "application/pdf");
}
