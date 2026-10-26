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

/// <summary>
/// A B2B tax invoice ready for the Invoice Registration Portal, as its module priced it.
/// <paramref name="Problem"/>: why it cannot be an e-invoice as it stands (a return line, for instance), checked before
/// the payload; null when it can.
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
    string? Problem = null);

/// <summary>A B2B tax invoice in a register of invoices that need an IRN.</summary>
public sealed record EInvoiceCandidate(string Source, Guid Id, string Number, DateOnly Date, string BuyerGstin, string BuyerName, decimal Total, bool IsCancelled);

/// <summary>
/// A module whose B2B tax invoices are e-invoices (GST rule 48(4)): bills to payers registered for GST, and the pharmacy's
/// B2B sales. Billing keeps the e-invoice register and reads every registered source, so it never references the others.
/// Implementations filter by the facility given and use the hospital's (IST) calendar day.
/// </summary>
public interface IEInvoiceSource
{
    /// <summary>Stable key: <c>bill</c> or <c>pharmacy</c>. It names the invoice in the register and in its address.</summary>
    string Source { get; }

    /// <summary>The B2B tax invoices of <paramref name="from"/> to <paramref name="to"/>, cancelled ones included.</summary>
    Task<IReadOnlyList<EInvoiceCandidate>> ListAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>The invoice, or null when there is none or it is not a B2B tax invoice.</summary>
    Task<EInvoiceDocument?> FindAsync(Guid id, CancellationToken cancellationToken);
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
