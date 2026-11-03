using FluentValidation;
using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.EInvoices;

/// <summary>Where a B2B tax invoice or credit note stands with the Invoice Registration Portal.</summary>
public enum EInvoiceState
{
    /// <summary>No IRN yet.</summary>
    Pending = 0,
    Registered,

    /// <summary>The IRN was cancelled on the portal: cancel the invoice here and issue a new one, under a new number.</summary>
    IrnCancelled,

    /// <summary>Cancelled here before it had an IRN: nothing to report.</summary>
    NotNeeded,
}

/// <param name="Applicable">E-invoicing applies to the facility (setting <c>gst.einvoice.from</c>); when not, the register is empty.</param>
/// <param name="ApplicableFrom">The first invoice date e-invoicing applies to.</param>
public sealed record EInvoiceRegister(bool Applicable, DateOnly? ApplicableFrom, DateOnly From, DateOnly To, IReadOnlyList<EInvoiceRow> Rows);

/// <param name="Source"><c>bill</c>, <c>creditnote</c> (a bill's credit note), <c>pharmacy</c> or <c>pharmacyreturn</c>.</param>
/// <param name="ReportBy">Thirty days after the document's date: the latest the portal takes it from a large taxpayer.</param>
/// <param name="Type"><c>INV</c> for a tax invoice, <c>CRN</c> for a credit note.</param>
/// <param name="Against">For a credit note: the number of the invoice it reduces.</param>
public sealed record EInvoiceRow(
    string Source,
    Guid Id,
    string Number,
    DateOnly Date,
    string BuyerGstin,
    string BuyerName,
    decimal Total,
    EInvoiceState State,
    DateOnly ReportBy,
    bool IsOverdue,
    string? Irn,
    string? AckNo,
    DateTimeOffset? AckDate,
    string Type = EInvoiceDocumentTypes.Invoice,
    string? Against = null);

public sealed record EInvoiceRegistrationDto(
    string Irn,
    string AckNo,
    DateTimeOffset AckDate,
    EInvoiceStatus Status,
    string RecordedByName,
    DateTimeOffset RecordedAt,
    DateTimeOffset? CancelledAt,
    EInvoiceCancelReason? CancelReason,
    string? CancelRemark,
    DateTimeOffset CancelBy,
    bool CanCancel,
    string QrCodeSvg,
    uint Version);

/// <param name="Problems">What stops the portal from registering the document; empty when its JSON is ready.</param>
/// <param name="Registration">The latest IRN recorded for it, live or cancelled.</param>
/// <param name="Type"><c>INV</c> for a tax invoice, <c>CRN</c> for a credit note.</param>
/// <param name="Against">For a credit note: the number of the invoice it reduces, and its date.</param>
public sealed record EInvoiceDetail(
    string Source,
    Guid Id,
    string Number,
    DateOnly Date,
    string BuyerGstin,
    string BuyerName,
    string PlaceOfSupply,
    int Lines,
    decimal Total,
    EInvoiceState State,
    IReadOnlyList<string> Problems,
    EInvoiceRegistrationDto? Registration,
    string Type = EInvoiceDocumentTypes.Invoice,
    string? Against = null,
    DateOnly? AgainstDate = null);

/// <summary>The IRN the portal gave, as its acknowledgement shows: the acknowledgement number and the signed QR code.</summary>
public sealed record RecordIrnRequest(string AckNo, string SignedQrCode);

public sealed record CancelIrnRequest(EInvoiceCancelReason Reason, string Remark, uint Version);

public sealed class RecordIrnRequestValidator : AbstractValidator<RecordIrnRequest>
{
    public RecordIrnRequestValidator()
    {
        RuleFor(x => x.AckNo).NotEmpty().Matches("^[0-9]{10,20}$").WithMessage("The acknowledgement number is the portal's 10 to 20 digits.");
        RuleFor(x => x.SignedQrCode).NotEmpty().MaximumLength(8000);
    }
}

public sealed class CancelIrnRequestValidator : AbstractValidator<CancelIrnRequest>
{
    public CancelIrnRequestValidator()
    {
        RuleFor(x => x.Reason).IsInEnum();
        RuleFor(x => x.Remark).NotEmpty().MaximumLength(100);
    }
}
