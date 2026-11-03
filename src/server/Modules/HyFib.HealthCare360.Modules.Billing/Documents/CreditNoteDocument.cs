using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Billing.Documents;

/// <summary>Who a credit note is issued to: the buyer the bill's tax invoice named (a registered payer, with its GSTIN), or the patient.</summary>
internal sealed record CreditNoteRecipient(string Name, string? Gstin, string? Address);

/// <summary>
/// The printed credit note (MB-05), with what CGST rule 53(1A) asks a credit note to carry: the hospital's name, address
/// and GSTIN (the letterhead), the words "Credit note", its number and date, the recipient (a payer registered for GST
/// with its GSTIN and address; the patient otherwise), the number and date of the tax invoice it reduces, each service it
/// corrects with its taxable value, GST rate and the GST credited, the reason, and the signature. A credit note the
/// Invoice Registration Portal registered carries its IRN and signed QR code (rule 48(4)); one issued after the bill's GST
/// could be reduced says that it lowers the amount and not the GST (s.34(2)). A write-off prints as a write-off note, which
/// is not a tax document. The counter prints it in English: it is a GST document.
/// </summary>
internal static class CreditNoteDocument
{
    private const string Styles = """
        .note { font-size: 12px; }
        .einvoice { display: flex; gap: 10px; align-items: center; margin: 6px 0; font-size: 10px; }
        .einvoice-qr { width: 32mm; height: 32mm; }
        .irn { font-family: monospace; word-break: break-all; }
        .hdr td { vertical-align: top; }
        .h-org { display: flex; gap: 10px; align-items: flex-start; }
        .h-logo { max-height: 18mm; max-width: 40mm; object-fit: contain; }
        .h-name { font-size: 15px; font-weight: 700; color: #045E80; }
        .h-title { font-size: 16px; font-weight: 700; text-transform: uppercase; letter-spacing: .5px; }
        .box { border: 1px solid #bbb; border-radius: 4px; padding: 6px 8px; margin: 8px 0; }
        .grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: 2px 16px; }
        table.lines { margin-top: 6px; }
        table.lines th { border-bottom: 1px solid #555; font-size: 10.5px; background: #f2f2f2; }
        table.lines td { border-bottom: 1px solid #e3e3e3; font-size: 10.5px; }
        .num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
        .totals { width: 55%; margin-left: auto; margin-top: 8px; }
        .totals td { padding: 2px 4px; }
        .totals .strong td { font-weight: 700; border-top: 1px solid #555; font-size: 13px; }
        .words { margin-top: 6px; font-style: italic; }
        .sign { margin-top: 36px; display: flex; justify-content: flex-end; }
        .sign div { border-top: 1px solid #555; padding-top: 2px; min-width: 180px; text-align: center; }
        .foot { margin-top: 12px; font-size: 9.5px; color: #555; }

        """;

    /// <summary>The note under the letterhead of the facility that issued its bill.</summary>
    public static async Task<PrintableDocument> BuildAsync(
        BillDetail bill, CreditNoteDto note, CreditNoteRecipient recipient, DocumentAssets assets, CancellationToken cancellationToken) =>
        Build(bill, note, recipient, await assets.LetterheadAsync(bill.FacilityId, PrintTemplateCatalog.Bill, cancellationToken));

    public static PrintableDocument Build(BillDetail bill, CreditNoteDto note, CreditNoteRecipient recipient, DocumentLetterhead letterhead)
    {
        var writeOff = note.Kind == CreditNoteKind.WriteOff;
        var title = writeOff ? "Write-off note" : "Credit note";
        var invoiced = bill.FinalizedAt ?? bill.CreatedAt;
        return new PrintableDocument
        {
            Title = $"{title} {note.Number}",
            FileName = $"{(writeOff ? "write-off" : "credit-note")}-{note.Number}",
            Footer = string.Join(" · ", new[] { bill.PatientName, bill.Uhid is { } uhid ? $"UHID {uhid}" : null, $"{title} {note.Number}" }
                .Where(part => !string.IsNullOrWhiteSpace(part))),
            Language = DocumentText.English.Language,
            Styles = Styles,
            Template = letterhead.Template,
            Body = Html.Of($"""
                <div class="note">
                  {BillDocument.Header(letterhead, title, [("Number", note.Number), ("Date", DocumentFormat.DateTime(note.IssuedAt))])}
                  <div class="box grid">
                    <div>Issued to: <b>{recipient.Name}</b>{(recipient.Gstin is { } gstin ? Html.Of($"<div>GSTIN <b>{gstin}</b></div>") : Html.Empty)}{(recipient.Address is { } address ? Html.Of($"<div>{address}</div>") : Html.Empty)}</div>
                    <div>
                      <div>Against {(bill.IsTaxInvoice ? "tax invoice" : "bill")}: <b>{bill.BillNumber ?? DocumentFormat.Missing}</b> of {DocumentFormat.Date(BillingTime.LocalDate(invoiced))}</div>
                      <div>Patient: {bill.PatientName ?? DocumentFormat.Missing} · UHID {bill.Uhid ?? DocumentFormat.Missing}</div>
                      {(bill.IsTaxInvoice && bill.PlaceOfSupply is { } place ? Html.Of($"<div>Place of supply: {Gstin.States.GetValueOrDefault(place, place)} ({place})</div>") : Html.Empty)}
                    </div>
                  </div>
                  {EInvoiceBox(note)}
                  {(writeOff ? WriteOff(note) : Lines(bill, note))}
                  <div class="box">Reason: {note.Reason}</div>
                  {Gst(bill, note)}
                  <div class="keep">
                    <div class="sign"><div>Authorised signatory</div></div>
                    <div class="foot">{Asked(note)} This is a computer-generated {title.ToLowerInvariant()}.{(letterhead.Note is { } footnote ? $" {footnote}" : "")}</div>
                  </div>
                </div>
                """),
        };
    }

    /// <summary>The IRN the portal gave the credit note (a <c>CRN</c>), its acknowledgement and the signed QR code it must carry.</summary>
    private static Html EInvoiceBox(CreditNoteDto note) => note.EInvoice is not { } e
        ? Html.Empty
        : Html.Of($"""
            <div class="einvoice keep">
              {Html.Image(ImageData.FromDataUri(e.QrCodeSvg), "E-invoice QR code", "einvoice-qr")}
              <div>
                <div>IRN: <span class="irn">{e.Irn}</span></div>
                <div>Ack. no.: {e.AckNo} · Ack. date: {DocumentFormat.DateTime(e.AckDate)}</div>
              </div>
            </div>
            """);

    private static Html Lines(BillDetail bill, CreditNoteDto note)
    {
        var lines = note.Lines ?? [];
        if (lines.Count == 0)
        {
            // A note that names no services: issued before they were named, or on a bill without GST.
            return Html.Of($"""
                <table class="totals"><tr class="strong"><td>Credited on bill {bill.BillNumber ?? DocumentFormat.Missing}</td><td class="num">{DocumentFormat.Money(note.Amount)}</td></tr></table>
                <div class="words">{AmountInWords.Rupees(note.Amount)}</div>
                """);
        }

        var igst = lines.Any(l => l.Igst != 0) || (bill.IsInterState && lines.All(l => l.Cgst == 0 && l.Sgst == 0));
        var taxHead = igst
            ? Html.Of($"<th class=\"num\">IGST</th>")
            : Html.Of($"<th class=\"num\">CGST</th><th class=\"num\">SGST</th>");
        var rows = lines.Select((line, index) => Html.Of($"""
            <tr>
              <td>{index + 1}</td>
              <td>{line.Description}</td>
              <td>{line.SacCode ?? DocumentFormat.Missing}</td>
              <td class="num">{Rate(line.GstRate)}</td>
              <td class="num">{DocumentFormat.Money(line.TaxableAmount)}</td>
              {(igst ? Html.Of($"<td class=\"num\">{DocumentFormat.Money(line.Igst)}</td>")
                  : Html.Of($"<td class=\"num\">{DocumentFormat.Money(line.Cgst)}</td><td class=\"num\">{DocumentFormat.Money(line.Sgst)}</td>"))}
              <td class="num">{DocumentFormat.Money(line.Amount)}</td>
            </tr>
            """));
        var gstRows = igst
            ? Html.Of($"<tr><td>IGST credited</td><td class=\"num\">{DocumentFormat.Money(lines.Sum(l => l.Igst))}</td></tr>")
            : Html.Of($"""
                <tr><td>CGST credited</td><td class="num">{DocumentFormat.Money(lines.Sum(l => l.Cgst))}</td></tr>
                <tr><td>SGST credited</td><td class="num">{DocumentFormat.Money(lines.Sum(l => l.Sgst))}</td></tr>
                """);
        return Html.Of($"""
            <table class="lines">
              <thead><tr><th>#</th><th>Service credited</th><th>SAC</th><th class="num">GST rate</th><th class="num">Taxable value</th>{taxHead}<th class="num">Credited</th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
            <div class="keep">
              <table class="totals">
                <tr><td>Taxable value</td><td class="num">{DocumentFormat.Money(note.TaxableAmount)}</td></tr>
                {gstRows}
                <tr class="strong"><td>Total credited</td><td class="num">{DocumentFormat.Money(note.Amount)}</td></tr>
              </table>
              <div class="words">{AmountInWords.Rupees(note.Amount)}</div>
            </div>
            """);
    }

    private static Html WriteOff(CreditNoteDto note) => Html.Of($"""
        <table class="totals"><tr class="strong"><td>Written off</td><td class="num">{DocumentFormat.Money(note.Amount)}</td></tr></table>
        <div class="words">{AmountInWords.Rupees(note.Amount)}</div>
        """);

    /// <summary>What the note did to GST, said once: a write-off reduces none, a late credit note neither.</summary>
    private static Html Gst(BillDetail bill, CreditNoteDto note)
    {
        if (note.Kind == CreditNoteKind.WriteOff)
        {
            return Html.Of($"<div class=\"foot\">A due that cannot be recovered, written off. It is not a GST credit note: it reduces no tax.</div>");
        }

        if (note.GstTimeBarred)
        {
            var lastDay = GstCreditNotes.LastDay(BillingTime.LocalDate(bill.FinalizedAt ?? bill.CreatedAt));
            return Html.Of($"<div class=\"box\"><b>GST not reduced.</b> The time to reduce the GST of this bill ended on {DocumentFormat.Date(lastDay)} (CGST Act 2017, s.34(2)); this note lowers the amount owed only.</div>");
        }

        return Html.Empty;
    }

    private static string Asked(CreditNoteDto note) => note.RequestedByName is { } asked
        ? $"Asked for by {asked}, approved by {note.IssuedByName ?? DocumentFormat.Missing}."
        : note.IssuedByName is { } issued ? $"Issued by {issued}." : string.Empty;

    private static string Rate(decimal rate) => rate.ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
