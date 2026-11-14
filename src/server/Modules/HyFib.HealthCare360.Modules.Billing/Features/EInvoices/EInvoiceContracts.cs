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
    uint Version,
    string Provider = EInvoice.ManualProvider,
    long? EwbNo = null,
    DateTimeOffset? EwbDate = null,
    DateTimeOffset? EwbValidTill = null);

/// <param name="Problems">What stops the portal from registering the document; empty when its JSON is ready.</param>
/// <param name="Registration">The latest IRN recorded for it, live or cancelled.</param>
/// <param name="Type"><c>INV</c> for a tax invoice, <c>CRN</c> for a credit note.</param>
/// <param name="Against">For a credit note or debit note: the number of the invoice it changes, and its date.</param>
/// <param name="PortalConnected">The facility is connected to the e-invoice portal: HC360 asks for the IRN itself.</param>
/// <param name="EwayBillNeeded">An invoice of goods above the e-way bill threshold (CGST rule 138).</param>
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
    DateOnly? AgainstDate = null,
    bool PortalConnected = false,
    bool EwayBillNeeded = false);

/// <summary>The IRN the portal gave, as its acknowledgement shows: the acknowledgement number and the signed QR code.</summary>
public sealed record RecordIrnRequest(string AckNo, string SignedQrCode);

/// <param name="CancelledOnPortal">The IRN was cancelled on the portal by hand: record it without asking the portal.</param>
public sealed record CancelIrnRequest(EInvoiceCancelReason Reason, string Remark, uint Version, bool CancelledOnPortal = false);

/// <summary>How the goods move, as the e-way bill names it (the API's <c>TransMode</c>).</summary>
public enum TransportMode
{
    Road = 1,
    Rail = 2,
    Air = 3,
    Ship = 4,
}

/// <summary>
/// The transport of an e-way bill generated from an IRN. <paramref name="DistanceKm"/>: 0 lets the portal work it out from
/// the PIN codes. Either the vehicle (Part-B) or the transporter's GSTIN or id (Part-A, the transporter fills Part-B).
/// </summary>
public sealed record GenerateEwayBillRequest(
    int DistanceKm,
    TransportMode Mode,
    string? VehicleNumber,
    string? TransporterGstin,
    string? TransporterName,
    string? TransportDocumentNumber,
    DateOnly? TransportDocumentDate,
    bool OverDimensionalCargo = false);

/// <summary>
/// An e-way bill generated outside HC360, as its print (EWB-01) shows it: its number (12 digits, or 14 for one generated
/// from 1 January 2027), when it was generated, and the end of its validity, blank while only its Part-A is filled.
/// </summary>
public sealed record RecordEwayBillRequest(string EwbNo, DateTimeOffset GeneratedAt, DateTimeOffset? ValidUntil);

public sealed class RecordEwayBillRequestValidator : AbstractValidator<RecordEwayBillRequest>
{
    public RecordEwayBillRequestValidator()
    {
        RuleFor(x => x.EwbNo).Must(n => EwayBillEntries.Number(n) is not null)
            .WithMessage("The e-way bill number is the portal's 12 digits (14 for one generated from 1 January 2027).");
        RuleFor(x => x.GeneratedAt).NotEmpty().WithMessage("Enter when the e-way bill was generated, as its print shows.");
    }
}

public sealed class RecordIrnRequestValidator : AbstractValidator<RecordIrnRequest>
{
    public RecordIrnRequestValidator()
    {
        RuleFor(x => x.AckNo).NotEmpty().Matches("^[0-9]{10,20}$").WithMessage("The acknowledgement number is the portal's 10 to 20 digits.");
        RuleFor(x => x.SignedQrCode).NotEmpty().MaximumLength(8000);
    }
}

public sealed class GenerateEwayBillRequestValidator : AbstractValidator<GenerateEwayBillRequest>
{
    public GenerateEwayBillRequestValidator()
    {
        RuleFor(x => x.DistanceKm).InclusiveBetween(0, 4000).WithMessage("The distance is up to 4000 km (0: the portal works it out from the PIN codes).");
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.VehicleNumber).Matches("^[A-Za-z0-9 ]{4,20}$").When(x => !string.IsNullOrWhiteSpace(x.VehicleNumber))
            .WithMessage("Enter the vehicle number as on its registration, for example KA01AB1234.");
        RuleFor(x => x.TransporterGstin).Matches("^[0-9A-Za-z]{15}$").When(x => !string.IsNullOrWhiteSpace(x.TransporterGstin))
            .WithMessage("The transporter's GSTIN or transporter id has 15 characters.");
        RuleFor(x => x.TransporterName).MaximumLength(100);
        RuleFor(x => x.TransportDocumentNumber).MaximumLength(15);
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.VehicleNumber) || !string.IsNullOrWhiteSpace(x.TransporterGstin))
            .WithName("vehicleNumber").WithMessage("Enter the vehicle number, or the transporter's GSTIN so the transporter fills it in.");
        RuleFor(x => x.VehicleNumber).NotEmpty().When(x => x.Mode == TransportMode.Road && string.IsNullOrWhiteSpace(x.TransporterGstin))
            .WithMessage("Goods by road need the vehicle number.");
        RuleFor(x => x.TransportDocumentNumber).NotEmpty().When(x => x.Mode != TransportMode.Road)
            .WithMessage("Goods by rail, air or ship need the transport document's number.");
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
