using FluentValidation;
using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.EInvoices;

/// <summary>Where a B2B tax invoice stands with the Invoice Registration Portal.</summary>
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

/// <param name="Source"><c>bill</c> or <c>pharmacy</c>.</param>
/// <param name="ReportBy">Thirty days after the invoice date: the latest the portal takes it from a large taxpayer.</param>
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
    DateTimeOffset? AckDate);

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

/// <param name="Problems">What stops the portal from registering the invoice; empty when its JSON is ready.</param>
/// <param name="Registration">The latest IRN recorded for it, live or cancelled.</param>
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
    EInvoiceRegistrationDto? Registration);

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
