using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>The buyer of a B2B invoice, as the e-invoice (INV-01) names them. <paramref name="Address"/> is free text with the PIN code in it.</summary>
public sealed record EInvoiceBuyer(string Gstin, string LegalName, string? Address, string? Phone, string? Email);

/// <summary>
/// One line of a B2B invoice in the e-invoice's terms: <paramref name="TotalAmount"/> is quantity × unit price before
/// tax, <paramref name="AssessableValue"/> the taxable value after <paramref name="Discount"/>, and
/// <paramref name="TotalItemValue"/> the value with GST.
/// </summary>
public sealed record EInvoiceItem(
    string Description,
    bool IsService,
    string? HsnCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalAmount,
    decimal Discount,
    decimal AssessableValue,
    decimal GstRate,
    decimal Igst,
    decimal Cgst,
    decimal Sgst,
    decimal TotalItemValue);

/// <summary>The e-invoice's document type, the portal's <c>DocDtls.Typ</c>.</summary>
public static class EInvoiceDocumentTypes
{
    /// <summary>A tax invoice.</summary>
    public const string Invoice = "INV";

    /// <summary>A credit note against a tax invoice (GST rule 48(4) makes it an e-invoice too).</summary>
    public const string CreditNote = "CRN";

    /// <summary>A debit note (supplementary invoice) against a tax invoice, raising what it charged (CGST Act s.34(3)).</summary>
    public const string DebitNote = "DBN";
}

/// <summary>
/// The tax invoice a credit note reduces, as the portal's <c>PrecDocDtls</c> names it (number and date), and where HC360
/// keeps it (<paramref name="Source"/> and <paramref name="Id"/>), so its own IRN can be looked up.
/// </summary>
public sealed record EInvoicePreceding(string Source, Guid Id, string Number, DateOnly Date);

/// <summary>
/// A B2B tax invoice or credit note ready for the Invoice Registration Portal, as its module priced it.
/// <paramref name="Problem"/>: why it cannot be an e-invoice as it stands (a return line, for instance), checked before
/// the payload; null when it can. <paramref name="Type"/>: <see cref="EInvoiceDocumentTypes"/>; a credit note names the
/// invoice it reduces in <paramref name="Preceding"/>.
/// </summary>
public sealed record EInvoiceDocument(
    string Source,
    Guid Id,
    Guid FacilityId,
    string Number,
    DateOnly Date,
    EInvoiceBuyer Buyer,
    string PlaceOfSupply,
    IReadOnlyList<EInvoiceItem> Items,
    decimal RoundOff,
    decimal Total,
    bool IsCancelled,
    string? Problem = null,
    string Type = EInvoiceDocumentTypes.Invoice,
    EInvoicePreceding? Preceding = null);

/// <summary>
/// A B2B tax invoice or credit note in a register of documents that need an IRN; a credit note names the invoice it
/// reduces (<paramref name="PrecedingNumber"/>).
/// </summary>
public sealed record EInvoiceCandidate(
    string Source,
    Guid Id,
    string Number,
    DateOnly Date,
    string BuyerGstin,
    string BuyerName,
    decimal Total,
    bool IsCancelled,
    string Type = EInvoiceDocumentTypes.Invoice,
    string? PrecedingNumber = null);

/// <summary>
/// A module whose B2B tax invoices or credit notes are e-invoices (GST rule 48(4)): bills to payers registered for GST
/// and the credit notes on them, and the pharmacy's B2B sales and their returns. Billing keeps the e-invoice register and
/// reads every registered source, so it never references the others. Implementations filter by the facility given and use
/// the hospital's (IST) calendar day.
/// </summary>
public interface IEInvoiceSource
{
    /// <summary>
    /// Stable key: <c>bill</c>, <c>creditnote</c>, <c>pharmacy</c> or <c>pharmacyreturn</c>. It names the document in the
    /// register and in its address.
    /// </summary>
    string Source { get; }

    /// <summary>The B2B documents of <paramref name="from"/> to <paramref name="to"/>, cancelled ones included.</summary>
    Task<IReadOnlyList<EInvoiceCandidate>> ListAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>The document, or null when there is none or it is not a B2B e-invoice.</summary>
    Task<EInvoiceDocument?> FindAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// Asks the e-invoice register (kept by Billing) to get a document's IRN on its own once the document is final (GST rule
/// 48(4)): the request goes out through the outbox when the caller's transaction commits, and the portal is asked when the
/// facility is connected to it. A module calls it in the transaction that makes one of its B2B documents final; a document
/// dated before e-invoicing applies is let go.
/// </summary>
public interface IEInvoiceQueue
{
    /// <param name="source">The document's <see cref="IEInvoiceSource.Source"/>.</param>
    /// <param name="date">The document's date, the hospital's (IST) day.</param>
    Task QueueAsync(string source, Guid id, Guid facilityId, DateOnly date, CancellationToken cancellationToken);
}

/// <summary>The IRN the portal gave an invoice, for its print. <paramref name="QrCodeSvg"/>: the signed QR code as an SVG data URI.</summary>
public sealed record EInvoiceStamp(string Irn, string AckNo, DateTimeOffset AckDate, string SignedQrCode, string QrCodeSvg);

/// <summary>
/// The e-invoice register (kept by Billing), for the modules whose invoices it holds: the IRN to print, and whether one is
/// live, so the invoice is not cancelled here while the portal still holds it.
/// </summary>
public interface IEInvoiceRegister
{
    /// <summary>The live (not cancelled) IRN of the invoice, if it has one.</summary>
    Task<EInvoiceStamp?> FindAsync(string source, Guid id, CancellationToken cancellationToken);
}

/// <summary>Whether the facility is connected to the Invoice Registration Portal, through which provider, or why not.</summary>
/// <param name="IsSimulator">The training simulator, which only a training or test server can use: its IRNs are made up.</param>
public sealed record IrpConnection(bool Connected, string? Provider, bool IsSimulator, string? Problem);

/// <summary>
/// The IRN the portal gave a document (NIC e-invoice API v1.03, <c>Invoice</c> and <c>Invoice/irn</c>): the acknowledgement,
/// the signed invoice and the signed QR code (both JWTs signed by the portal), and the e-way bill generated with it, if any.
/// <paramref name="Status"/> is how the portal holds the IRN now, as its IRN details say it: <see cref="ActiveStatus"/> or
/// <see cref="CancelledStatus"/>; null when the answer did not say, which confirms neither. <paramref name="CancelledAt"/>:
/// when the portal cancelled it, where its answer gives the time. The details of a cancelled IRN may come without the acknowledgement or the QR code:
/// then they are empty.
/// </summary>
public sealed record IrpIrn(
    string Irn,
    string AckNo,
    DateTimeOffset AckDate,
    string SignedInvoice,
    string SignedQrCode,
    long? EwbNo = null,
    DateTimeOffset? EwbDate = null,
    DateTimeOffset? EwbValidTill = null,
    string? Status = null,
    DateTimeOffset? CancelledAt = null)
{
    /// <summary>The portal's status of a live IRN.</summary>
    public const string ActiveStatus = "ACT";

    /// <summary>The portal's status of a cancelled IRN.</summary>
    public const string CancelledStatus = "CNL";

    /// <summary>The portal holds the IRN as active: its answer says so.</summary>
    public bool IsActive => string.Equals(Status, ActiveStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>The portal holds the IRN as cancelled.</summary>
    public bool IsCancelled => string.Equals(Status, CancelledStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How long after its generation the portal still gives an IRN's details (<c>Invoice/irn</c>). GSP documentation of the
    /// NIC API gives three days; HC360 asks within two, so a lookup it relies on is never one the portal no longer answers.
    /// Not yet confirmed on NIC's sandbox.
    /// </summary>
    public static readonly TimeSpan DetailsWindow = TimeSpan.FromDays(2);
}

/// <summary>The portal's answer to a cancellation: the IRN and when it was cancelled.</summary>
public sealed record IrpCancellation(string Irn, DateTimeOffset CancelledAt);

/// <summary>
/// An e-way bill generated from an IRN (NIC API <c>ewaybill</c>, or read back by <c>ewaybill/irn/{irn}</c>): its number,
/// date and the last moment it is valid, when the portal gives one. <paramref name="Status"/>, read back only: how the
/// portal holds it now, with the IRN's codes (<see cref="IrpIrn.ActiveStatus"/>, <see cref="IrpIrn.CancelledStatus"/>);
/// null when the answer did not say, which confirms neither. A cancelled one may come without its date: then it is
/// <see cref="DateTimeOffset.MinValue"/>.
/// </summary>
public sealed record IrpEwayBill(long EwbNo, DateTimeOffset EwbDate, DateTimeOffset? ValidTill, string? Status = null)
{
    /// <summary>The portal holds the e-way bill as live: its answer says so.</summary>
    public bool IsActive => string.Equals(Status, IrpIrn.ActiveStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>The portal holds the e-way bill as cancelled (on the e-way bill portal, within 24 hours of it: CGST rule 138(9)).</summary>
    public bool IsCancelled => string.Equals(Status, IrpIrn.CancelledStatus, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The portal refused the call: its error code (2150 duplicate IRN, 2270 past the cancellation window, and so on) and
/// message. <paramref name="ExistingIrn"/>: for a duplicate, the IRN the portal already holds for the document.
/// </summary>
public sealed record IrpError(string Code, string Message, string? ExistingIrn = null)
{
    /// <summary>The portal's code for a document it already gave an IRN.</summary>
    public const string DuplicateIrn = "2150";

    /// <summary>The portal's code for a cancellation asked for more than 24 hours after the IRN was generated.</summary>
    public const string CancelWindowCrossed = "2270";

    /// <summary>The portal's code for an e-way bill asked for from an IRN that already has one ("EwayBill is already generated for this IRN").</summary>
    public const string EwayBillExists = "4002";

    /// <summary>The portal's code when asked for the e-way bill of an IRN it holds none for ("Eway Bill details are not found").</summary>
    public const string EwayBillNotFound = "4005";

    /// <summary>Not a code of the portal's: the call did not reach it or got no answer.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>Not a code of the portal's: the connection is not set up or not allowed.</summary>
    public const string NotConnected = "not_connected";
}

/// <summary>A portal call's answer: the value, or the portal's error.</summary>
public sealed record IrpResult<T>(T? Value, IrpError? Error) where T : class
{
    public bool IsSuccess => Error is null && Value is not null;

    public static IrpResult<T> Ok(T value) => new(value, null);

    public static IrpResult<T> Fail(string code, string message, string? existingIrn = null) => new(null, new IrpError(code, message, existingIrn));
}

/// <summary>
/// The Invoice Registration Portal (GST rule 48(4)), reached directly (NIC) or through a GST suvidha provider, configured
/// per facility under Administration → Integrations (<c>integrations.irp</c>). It takes the e-invoice JSON Billing builds
/// (INV-01) and answers with the IRN and the signed QR code; it cancels an IRN within 24 hours; it gives an IRN's details
/// back; it generates an e-way bill for goods from an IRN, and gives that e-way bill back. The authentication token is the
/// provider's business.
/// <paramref name="sellerGstin"/> on every call is the GSTIN the facility registers under, which the portal's credentials
/// are for.
/// </summary>
public interface IInvoiceRegistrationPortal
{
    Task<IrpConnection> ConnectionAsync(Guid facilityId, CancellationToken cancellationToken);

    /// <summary>Asks for the IRN of the document in <paramref name="payload"/> (one INV-01 object, not a list).</summary>
    Task<IrpResult<IrpIrn>> GenerateAsync(Guid facilityId, string sellerGstin, JsonObject payload, CancellationToken cancellationToken);

    /// <summary>Cancels an IRN, with the portal's reason code (1 duplicate, 2 data entry mistake, 3 order cancelled, 4 other).</summary>
    Task<IrpResult<IrpCancellation>> CancelAsync(Guid facilityId, string sellerGstin, string irn, int reason, string remark, CancellationToken cancellationToken);

    Task<IrpResult<IrpIrn>> GetByIrnAsync(Guid facilityId, string sellerGstin, string irn, CancellationToken cancellationToken);

    /// <summary>Generates the e-way bill of a registered IRN; <paramref name="request"/> is the API's <c>ewaybill</c> body.</summary>
    Task<IrpResult<IrpEwayBill>> GenerateEwayBillAsync(Guid facilityId, string sellerGstin, JsonObject request, CancellationToken cancellationToken);

    /// <summary>
    /// The e-way bill the portal holds for <paramref name="irn"/>, with its status (NIC API <c>ewaybill/irn/{irn}</c>);
    /// refused with <see cref="IrpError.EwayBillNotFound"/> when it holds none.
    /// </summary>
    Task<IrpResult<IrpEwayBill>> GetEwayBillAsync(Guid facilityId, string sellerGstin, string irn, CancellationToken cancellationToken);
}
