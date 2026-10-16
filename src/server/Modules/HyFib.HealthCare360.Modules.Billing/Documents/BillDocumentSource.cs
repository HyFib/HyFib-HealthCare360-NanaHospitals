using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Billing.Documents;

/// <summary>
/// The A4 bill as a PDF, for the "bill shared" WhatsApp message (INT-WA): sent in the template's document header, and
/// attached to an e-mail should one carry it. It is the same document the counter prints and the portal downloads, made
/// when the message goes out, so it shows the payments received by then. Only an issued bill is sent: a draft is an
/// estimate, and a bill cancelled by then gives no document, so its message is never sent: a document header can't go
/// empty, and the outbox gives up on it when it expires.
/// </summary>
internal sealed class BillDocumentSource(HospitalDbContext db, DocumentAssets assets, IPdfRenderer renderer, TimeProvider time) : INotificationDocumentSource
{
    public const string DocumentKind = "billing.bill";

    public string Kind => DocumentKind;

    public async Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken)
    {
        var bill = await BillQueries.DetailAsync(db, id, time, cancellationToken);
        if (bill is null || !BillSharing.CanShare(bill.Status))
        {
            return null;
        }

        var document = await BillDocument.BuildAsync(bill, assets, cancellationToken);
        return new NotificationFile($"{document.FileName}.pdf", await renderer.RenderAsync(document.ToPdfSource(), cancellationToken), "application/pdf");
    }
}
