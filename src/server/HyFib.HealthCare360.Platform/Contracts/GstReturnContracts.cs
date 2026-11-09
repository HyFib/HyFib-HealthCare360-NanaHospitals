namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>What an outward document is in a GST return: a tax invoice or bill of supply, a credit note or a debit note.</summary>
public enum GstDocumentKind
{
    Invoice = 0,
    CreditNote,
    DebitNote,
}

/// <summary>How a line is taxed: taxable at its rate, nil-rated (0%), exempt (healthcare services, notification 12/2017) or outside GST.</summary>
public enum GstSupplyKind
{
    Taxable = 0,
    NilRated,
    Exempt,
    NonGst,
}

/// <summary>
/// One line of an outward document as a return counts it, at the values of the document (a credit note's are what it
/// takes off, all positive). <paramref name="Uqc"/> is GSTN's unit quantity code (<c>NOS</c> for medicines, <c>NA</c> for
/// services); <paramref name="Quantity"/> is 0 for services.
/// </summary>
public sealed record GstReturnLine(
    string? Hsn,
    string Description,
    bool IsService,
    string Uqc,
    decimal Quantity,
    decimal Rate,
    GstSupplyKind Supply,
    decimal Taxable,
    decimal Igst,
    decimal Cgst,
    decimal Sgst,
    decimal Cess = 0m);

/// <summary>
/// An outward document for GSTR-1 and GSTR-3B, by its own date. <paramref name="BuyerGstin"/> is null for a buyer not
/// registered (a patient). A note names the invoice it changes (<paramref name="OriginalNumber"/>, its date, value and
/// place of supply), which decides whether an unregistered buyer's note goes to CDNUR (the invoice was B2CL) or nets into
/// B2CS. <paramref name="Series"/> names the number series for the documents-issued table.
/// </summary>
public sealed record GstReturnDocument(
    string Source,
    Guid Id,
    GstDocumentKind Kind,
    string Number,
    DateOnly Date,
    string? BuyerGstin,
    string BuyerName,
    string PlaceOfSupply,
    decimal Value,
    bool IsCancelled,
    IReadOnlyList<GstReturnLine> Lines,
    string Series,
    string? OriginalNumber = null,
    DateOnly? OriginalDate = null,
    decimal? OriginalValue = null,
    string? OriginalPlaceOfSupply = null);

/// <summary>A purchase invoice the hospital received, for the input tax credit of GSTR-3B table 4 (supplier GSTIN null: unregistered, no credit).</summary>
public sealed record GstPurchase(
    string Source,
    Guid Id,
    string? SupplierGstin,
    string SupplierName,
    string InvoiceNumber,
    DateOnly Date,
    decimal Taxable,
    decimal Igst,
    decimal Cgst,
    decimal Sgst,
    decimal Cess = 0m);

/// <summary>
/// A module whose outward documents go into the hospital's GST returns: Billing's bills, credit and debit notes; the
/// pharmacy's sales and returns. Billing builds the returns and reads every registered source. Documents are those of the
/// facilities given (all registered under one GSTIN), dated from <c>from</c> to <c>to</c> (hospital calendar days),
/// cancelled ones included (they count in the documents issued).
/// </summary>
public interface IGstReturnSource
{
    Task<IReadOnlyList<GstReturnDocument>> OutwardAsync(IReadOnlyCollection<Guid> facilityIds, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>A module whose purchase invoices carry input tax: the stores' goods receipts.</summary>
public interface IGstPurchaseSource
{
    Task<IReadOnlyList<GstPurchase>> InwardAsync(IReadOnlyCollection<Guid> facilityIds, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
