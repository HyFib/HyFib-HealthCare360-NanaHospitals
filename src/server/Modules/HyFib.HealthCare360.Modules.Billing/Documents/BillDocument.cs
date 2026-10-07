using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Billing.Documents;

/// <summary>A row of the bill's totals. The strong rows are the amounts the patient acts on.</summary>
internal sealed record BillTotalRow(string Label, decimal Amount, bool Strong = false);

/// <summary>The GST of a tax invoice for one SAC code and rate. CGST and SGST are each charged at half the rate.</summary>
internal sealed record GstSummaryRow(string Sac, decimal Rate, decimal Taxable, decimal Cgst, decimal Sgst);

/// <summary>
/// The A4 bill (MB-05, OPB, IPB): a tax invoice when any line carries GST, otherwise a bill of supply (most healthcare
/// services are exempt), an IP interim or final bill, or the estimate printed from a draft. It shows the hospital's name,
/// address and tax ids with its logo, the patient and payer, every line with its SAC code, package, discount and GST, the
/// totals with the payer's and the patient's share, the amount in words, the GST summary, the payments received
/// (advances adjusted included), the signature lines and the footer notes. The counter prints this document and the
/// patient downloads it as a PDF, so both copies are the same. Ported from the staff app's A4 bill print; the 80 mm
/// counter copy stays in the browser.
/// </summary>
internal static class BillDocument
{
    // The staff app's A4 bill styles, with the logo added. The bill keeps that print's 12px base size (the reports use
    // 10pt), so the thirteen columns of a tax invoice fit across the page.
    private const string Styles = """
        .bill { font-size: 12px; }
        .hdr td { vertical-align: top; }
        .h-org { display: flex; gap: 10px; align-items: flex-start; }
        .h-logo { max-height: 18mm; max-width: 40mm; object-fit: contain; }
        .h-name { font-size: 15px; font-weight: 700; color: #045E80; }
        .h-title { font-size: 16px; font-weight: 700; text-transform: uppercase; letter-spacing: .5px; }
        .box { border: 1px solid #bbb; border-radius: 4px; padding: 6px 8px; margin: 8px 0; }
        .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 2px 16px; }
        table.lines { margin-top: 6px; }
        table.lines th { border-bottom: 1px solid #555; font-size: 10.5px; background: #f2f2f2; }
        table.lines td { border-bottom: 1px solid #e3e3e3; font-size: 10.5px; }
        .num { text-align: right; white-space: nowrap; font-variant-numeric: tabular-nums; }
        .totals { width: 55%; margin-left: auto; margin-top: 8px; }
        .totals td { padding: 2px 4px; }
        .totals .strong td { font-weight: 700; border-top: 1px solid #555; font-size: 13px; }
        .words { margin-top: 6px; font-style: italic; }
        .tag { display: inline-block; border: 1px solid #555; border-radius: 3px; padding: 0 4px; font-size: 9px; margin-left: 4px; }
        .sign { margin-top: 36px; display: flex; justify-content: space-between; }
        .sign div { border-top: 1px solid #555; padding-top: 2px; min-width: 180px; text-align: center; }
        .foot { margin-top: 12px; font-size: 9.5px; color: #555; }
        .stamp { position: fixed; top: 38%; left: 12%; font-size: 64px; color: rgba(200,0,0,.18); transform: rotate(-24deg); font-weight: 800; letter-spacing: 6px; }
        h3 { margin: 10px 0 4px; font-size: 12px; }

        """;

    /// <summary>The bill under the letterhead of the facility that issued it, as its bills template says (HMS-04).</summary>
    public static async Task<PrintableDocument> BuildAsync(BillDetail bill, DocumentAssets assets, CancellationToken cancellationToken) =>
        Build(bill, await assets.LetterheadAsync(bill.FacilityId, PrintTemplateCatalog.Bill, cancellationToken));

    public static PrintableDocument Build(BillDetail bill, DocumentLetterhead letterhead)
    {
        var title = Title(bill);
        var closing = $"This is a computer-generated {LowerFirst(title)}." + (letterhead.Note is { } note ? $" {note}" : "");
        var notes = string.IsNullOrWhiteSpace(bill.Notes) ? Html.Empty : Html.Of($"<div class=\"foot\">{Html.Lines(bill.Notes)}</div>");
        return new PrintableDocument
        {
            Title = bill.BillNumber is { } number ? $"{title} {number}" : title,
            FileName = FileName(bill),
            Footer = Footer(bill),
            Styles = Styles,
            Template = letterhead.Template,
            Body = Html.Of($"""
                <div class="bill">
                  {Stamp(bill)}
                  {Header(letterhead, title, [("Bill no.", bill.BillNumber ?? "Draft"), ("Date", DocumentFormat.DateTime(bill.FinalizedAt ?? bill.CreatedAt))])}
                  {PatientBox(bill)}
                  {Cancellation(bill)}
                  {LinesTable(bill)}
                  <div class="keep">
                    {TotalsTable(bill)}
                    <div class="words">Payable by patient: {AmountInWords.Rupees(bill.PatientAmount)}</div>
                  </div>
                  {GstTable(bill)}
                  {PaymentsTable(bill)}
                  {notes}
                  <div class="keep">
                    <div class="sign"><div>Patient / attendant</div><div>Authorised signatory</div></div>
                    <div class="foot">{closing}</div>
                  </div>
                </div>
                """),
        };
    }

    /// <summary>
    /// "Tax invoice" when any line carries GST, otherwise "Bill of supply"; IP bills add "IP bill" or "IP final bill".
    /// A draft is an estimate, not a bill.
    /// </summary>
    public static string Title(BillDetail bill)
    {
        if (bill.Status == BillStatus.Draft)
        {
            return "Estimate (draft bill)";
        }

        var kind = bill.IsTaxInvoice ? "Tax invoice" : "Bill of supply";
        return bill.Type == BillType.InPatient ? $"{kind} · {(bill.IsFinalBill ? "IP final bill" : "IP bill")}" : kind;
    }

    /// <summary>The word printed across every page of a copy that is not a valid bill.</summary>
    public static string? Watermark(BillDetail bill) => bill.Status switch
    {
        BillStatus.Cancelled or BillStatus.Refunded => "CANCELLED",
        BillStatus.Draft => "DRAFT",
        _ => null,
    };

    /// <summary>"bill-NHOB260000001"; a draft has no number yet: "bill-draft-NH26000123-2026-09-25".</summary>
    public static string FileName(BillDetail bill) => bill.BillNumber is { } number
        ? $"bill-{number}"
        : $"bill-draft-{bill.Uhid ?? "patient"}-{DocumentFormat.ToIst(bill.CreatedAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>"Ravi Kumar · UHID NH26000123 · Bill NHOB260000001", at the foot of every page.</summary>
    public static string Footer(BillDetail bill) => string.Join(" · ", new[]
    {
        bill.PatientName,
        bill.Uhid is { } uhid ? $"UHID {uhid}" : null,
        bill.BillNumber is { } number ? $"Bill {number}" : "Estimate (draft bill)",
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// From the gross amount to the net amount, the payer's and the patient's share, credits, and (once finalized) what
    /// was paid and what is still due or to be refunded.
    /// </summary>
    public static IReadOnlyList<BillTotalRow> Totals(BillDetail bill)
    {
        var rows = new List<BillTotalRow> { new("Gross amount", bill.GrossAmount) };
        if (bill.PackageCoveredAmount != 0)
        {
            rows.Add(new("Less: covered by package", -bill.PackageCoveredAmount));
        }

        if (bill.TariffDiscountAmount != 0)
        {
            rows.Add(new("Less: tariff concession", -bill.TariffDiscountAmount));
        }

        var staffDiscount = bill.LineDiscountAmount + bill.BillDiscountAmount;
        if (staffDiscount != 0)
        {
            rows.Add(new(string.IsNullOrWhiteSpace(bill.DiscountReason) ? "Less: discount" : $"Less: discount ({bill.DiscountReason})", -staffDiscount));
        }

        if (bill.IsTaxInvoice)
        {
            rows.Add(new("Taxable value", bill.TaxableAmount));
            rows.Add(new("CGST", bill.CgstAmount));
            rows.Add(new("SGST", bill.SgstAmount));
        }

        if (bill.RoundOffAmount != 0)
        {
            rows.Add(new("Round off", bill.RoundOffAmount));
        }

        rows.Add(new("Net amount", bill.NetAmount, Strong: true));
        if (bill.PayerAmount != 0)
        {
            rows.Add(new($"Payable by {bill.PayerName ?? "insurer"}", bill.PayerAmount));
            rows.Add(new("Payable by patient", bill.PatientAmount, Strong: true));
        }

        if (bill.CreditAmount != 0)
        {
            rows.Add(new("Credit notes / write-off", -bill.CreditAmount));
        }

        if (bill.Status != BillStatus.Draft)
        {
            rows.Add(new("Paid", bill.PaidAmount));
            if (bill.RefundedAmount != 0)
            {
                rows.Add(new("Refunded", -bill.RefundedAmount));
            }

            rows.Add(bill.RefundableAmount > 0
                ? new("Refundable to patient", bill.RefundableAmount, Strong: true)
                : new("Balance due", bill.DueAmount, Strong: true));
        }

        return rows;
    }

    /// <summary>GST by SAC code and rate, as a tax invoice must show it; exempt lines are left out.</summary>
    public static IReadOnlyList<GstSummaryRow> GstSummary(IEnumerable<BillLineDto> lines) =>
        lines.Where(l => l.TaxRate > 0)
            .GroupBy(l => (Sac: l.SacCode ?? "", l.TaxRate))
            .Select(g => new GstSummaryRow(
                g.Key.Sac.Length > 0 ? g.Key.Sac : DocumentFormat.Missing, g.Key.TaxRate, g.Sum(l => l.TaxableAmount), g.Sum(l => l.CgstAmount),
                g.Sum(l => l.SgstAmount)))
            .ToList();

    /// <summary>
    /// The bill's own header (not the reports' letterhead): logo, name, address, contact, tax ids and registration on the
    /// left; the title with the number and date on the right. On pre-printed stationery the left keeps only the tax ids,
    /// which a tax invoice must show whatever the paper has on it.
    /// </summary>
    public static Html Header(DocumentLetterhead letterhead, string title, IReadOnlyList<(string Label, string Value)> meta)
    {
        var contact = string.Join(" · ", new[] { letterhead.Phone is { } phone ? $"Phone {phone}" : null, letterhead.Email }.Where(c => c is not null));
        var tax = string.Join(" · ", new[] { letterhead.Gstin is { } gstin ? $"GSTIN {gstin}" : null, letterhead.Pan is { } pan ? $"PAN {pan}" : null }.Where(t => t is not null));
        if (!letterhead.Template.PrintsLetterhead)
        {
            return Html.Of($"""
                <table class="hdr"><tr>
                  <td>{Muted(tax)}</td>
                  <td class="right">
                    <div class="h-title">{title}</div>
                    {meta.Select(m => Html.Of($"<div>{m.Label}: <b>{m.Value}</b></div>"))}
                  </td>
                </tr></table>
                """);
        }

        return Html.Of($"""
            <table class="hdr"><tr>
              <td>
                <div class="h-org">
                  {Html.Image(letterhead.Logo, "", "h-logo")}
                  <div>
                    <div class="h-name">{letterhead.LegalName ?? letterhead.Name}</div>
                    {Muted(letterhead.Address)}
                    {Muted(contact)}
                    {Muted(tax)}
                    {Muted(letterhead.RegistrationNumber is { } registration ? $"Registration {registration}" : null)}
                  </div>
                </div>
              </td>
              <td class="right">
                <div class="h-title">{title}</div>
                {meta.Select(m => Html.Of($"<div>{m.Label}: <b>{m.Value}</b></div>"))}
              </td>
            </tr></table>
            """);
    }

    /// <summary>"Cash + Upi" (a receipt's mode summary) as words: "Cash + UPI".</summary>
    public static string ModesLabel(string? summary) => string.IsNullOrWhiteSpace(summary)
        ? DocumentFormat.Missing
        : string.Join(" + ", summary.Split('+', StringSplitOptions.TrimEntries).Select(ModeLabel));

    public static string StatusLabel(BillStatus status) => status switch
    {
        BillStatus.Finalized => "Unpaid",
        BillStatus.PartiallyPaid => "Partly paid",
        BillStatus.Refunded => "Cancelled, refunded",
        _ => status.ToString(),
    };

    public static string PayerLabel(PayerType payerType) => payerType switch
    {
        PayerType.Self => "Self pay",
        PayerType.GovernmentScheme => "Government scheme",
        _ => payerType.ToString(),
    };

    private static Html Stamp(BillDetail bill) => Watermark(bill) is { } stamp ? Html.Of($"<div class=\"stamp\">{stamp}</div>") : Html.Empty;

    private static Html PatientBox(BillDetail bill)
    {
        var tariff = string.IsNullOrWhiteSpace(bill.TariffPlanName) ? Html.Empty : Html.Of($"<div>Tariff: {bill.TariffPlanName}</div>");
        var coPay = bill.CoPayPercent > 0 ? Html.Of($"<div>Co-pay: {Percent(bill.CoPayPercent)}</div>") : Html.Empty;
        return Html.Of($"""
            <div class="box grid">
              <div>Patient: <b>{bill.PatientName ?? DocumentFormat.Missing}</b></div>
              <div>UHID: <b>{bill.Uhid ?? DocumentFormat.Missing}</b></div>
              <div>Status: {StatusLabel(bill.Status)}</div>
              <div>Payer: {bill.PayerName ?? PayerLabel(bill.PayerType)}</div>
              {tariff}
              {coPay}
            </div>
            """);
    }

    private static Html Cancellation(BillDetail bill) => bill.Status is BillStatus.Cancelled or BillStatus.Refunded
        ? Html.Of($"<div class=\"box\"><b>Cancelled</b> on {DocumentFormat.DateTime(bill.CancelledAt)}{(string.IsNullOrWhiteSpace(bill.CancelReason) ? "" : $": {bill.CancelReason}")}</div>")
        : Html.Empty;

    private static Html LinesTable(BillDetail bill)
    {
        // The package column only when a package covers something; the GST columns only on a tax invoice.
        var package = bill.Lines.Any(l => l.PackageCoveredAmount != 0);
        var tax = bill.IsTaxInvoice;
        var packageHead = package ? Html.Of($"<th class=\"num\">Package</th>") : Html.Empty;
        var taxHead = tax ? Html.Of($"<th class=\"num\">Taxable</th><th class=\"num\">GST</th><th class=\"num\">CGST</th><th class=\"num\">SGST</th>") : Html.Empty;
        var rows = bill.Lines.Select((line, index) => Html.Of($"""
            <tr>
              <td>{index + 1}</td>
              <td>{line.Description}{(line.IsReturn ? Html.Of($"<span class=\"tag\">Return</span>") : Html.Empty)}<div class="muted">{LineDetail(line)}</div></td>
              <td>{line.SacCode ?? DocumentFormat.Missing}</td>
              <td class="num">{Quantity(line.Quantity)}</td>
              <td class="num">{DocumentFormat.Money(line.UnitPrice)}</td>
              <td class="num">{DocumentFormat.Money(line.GrossAmount)}</td>
              {(package ? Html.Of($"<td class=\"num\">{DocumentFormat.Money(line.PackageCoveredAmount)}</td>") : Html.Empty)}
              <td class="num">{DocumentFormat.Money(line.TariffDiscountAmount + line.LineDiscountAmount + line.BillDiscountShare)}</td>
              {(tax ? TaxCells(line) : Html.Empty)}
              <td class="num">{DocumentFormat.Money(line.NetAmount)}</td>
            </tr>
            """));
        return Html.Of($"""
            <table class="lines">
              <thead><tr><th>#</th><th>Service</th><th>SAC</th><th class="num">Qty</th><th class="num">Rate</th><th class="num">Amount</th>{packageHead}<th class="num">Discount</th>{taxHead}<th class="num">Net</th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
            """);
    }

    private static Html TaxCells(BillLineDto line) => Html.Of(
        $"<td class=\"num\">{DocumentFormat.Money(line.TaxableAmount)}</td><td class=\"num\">{(line.TaxRate > 0 ? Percent(line.TaxRate) : "Exempt")}</td><td class=\"num\">{DocumentFormat.Money(line.CgstAmount)}</td><td class=\"num\">{DocumentFormat.Money(line.SgstAmount)}</td>");

    private static Html TotalsTable(BillDetail bill)
    {
        var rows = Totals(bill).Select(row => row.Strong
            ? Html.Of($"<tr class=\"strong\"><td>{row.Label}</td><td class=\"num\">{DocumentFormat.Money(row.Amount)}</td></tr>")
            : Html.Of($"<tr><td>{row.Label}</td><td class=\"num\">{DocumentFormat.Money(row.Amount)}</td></tr>"));
        return Html.Of($"<table class=\"totals\"><tbody>{rows}</tbody></table>");
    }

    private static Html GstTable(BillDetail bill)
    {
        var rows = bill.IsTaxInvoice ? GstSummary(bill.Lines) : [];
        return rows.Count == 0
            ? Html.Empty
            : Html.Of($"""
                <section class="keep">
                  <h3>GST summary</h3>
                  <table class="lines">
                    <thead><tr><th>SAC</th><th class="num">Taxable value</th><th class="num">CGST rate</th><th class="num">CGST</th><th class="num">SGST rate</th><th class="num">SGST</th></tr></thead>
                    <tbody>{rows.Select(g => Html.Of($"<tr><td>{g.Sac}</td><td class=\"num\">{DocumentFormat.Money(g.Taxable)}</td><td class=\"num\">{Percent(g.Rate / 2)}</td><td class=\"num\">{DocumentFormat.Money(g.Cgst)}</td><td class=\"num\">{Percent(g.Rate / 2)}</td><td class=\"num\">{DocumentFormat.Money(g.Sgst)}</td></tr>"))}</tbody>
                  </table>
                </section>
                """);
    }

    // Every receipt against the bill, including IP advances and patient advances adjusted ("Advance adjusted").
    private static Html PaymentsTable(BillDetail bill) => bill.Receipts.Count == 0
        ? Html.Empty
        : Html.Of($"""
            <h3>Payments received</h3>
            <table class="lines">
              <thead><tr><th>Receipt</th><th>Date</th><th>Mode</th><th class="num">Amount</th></tr></thead>
              <tbody>{bill.Receipts.Select(r => Html.Of($"<tr><td>{r.ReceiptNumber}</td><td>{DocumentFormat.DateTime(r.ReceivedAt)}</td><td>{ModesLabel(r.Modes)}</td><td class=\"num\">{DocumentFormat.Money(r.Amount)}</td></tr>"))}</tbody>
            </table>
            """);

    // "CONS-NEW · Dr. Asha Rao · 25 Sep 2026"
    private static string LineDetail(BillLineDto line) =>
        string.Join(" · ", new[] { line.ServiceCode, line.PractitionerName, DocumentFormat.Date(line.ServiceDate) }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string ModeLabel(string mode) => Enum.TryParse<PaymentMode>(mode, out var parsed) && Enum.IsDefined(parsed)
        ? parsed switch
        {
            PaymentMode.Upi => "UPI",
            PaymentMode.NetBanking => "Bank transfer",
            PaymentMode.PaymentGateway => "Online payment",
            PaymentMode.Insurance => "Insurance settlement",
            PaymentMode.Advance => "Advance adjusted",
            _ => parsed.ToString(),
        }
        : mode;

    // Quantities as the counter keys them: 1, 1.5, 0.25 (stored with three decimals).
    private static string Quantity(decimal quantity) => quantity.ToString("0.###", CultureInfo.InvariantCulture);

    // "18%", "2.5%": at most two decimals.
    private static string Percent(decimal percent) => percent.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    // "This is a computer-generated tax invoice · IP final bill": the title in the sentence, "IP" kept in capitals.
    private static string LowerFirst(string text) => text.Length == 0 ? text : $"{char.ToLowerInvariant(text[0])}{text.AsSpan(1)}";

    private static Html Muted(string? text) => string.IsNullOrWhiteSpace(text) ? Html.Empty : Html.Of($"<div class=\"muted\">{text}</div>");
}
