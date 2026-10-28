using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;

/// <summary>An outside (paper) prescription presented at the counter for prescription-only medicines.</summary>
public sealed record ExternalPrescription(string PrescriberName, string? PrescriberRegistrationNumber, DateOnly? PrescriptionDate, string? Reference);

public sealed record CounterSaleLine(Guid ItemId, decimal Quantity, Guid? BatchId);

/// <summary>
/// A buyer registered for GST (a clinic, a nursing home). <paramref name="DeliveryState"/>: the GST state code the goods
/// are dispatched to, when not handed over at the counter; another state's makes the supply inter-state (IGST).
/// <paramref name="Address"/>: the buyer's address with its PIN code, which the e-invoice needs.
/// </summary>
public sealed record B2bBuyer(string Gstin, string LegalName, string? DeliveryState, string? Address = null);

/// <param name="PatientId">Registered patient (enables credit and the medication history); walk-in customers leave it empty.</param>
/// <param name="Prescription">Required for Schedule H/H1/X medicines.</param>
public sealed record CounterSaleRequest(
    Guid StoreId,
    Guid? PatientId,
    string? CustomerName,
    string? CustomerPhone,
    ExternalPrescription? Prescription,
    IReadOnlyList<CounterSaleLine> Lines,
    decimal DiscountPercent,
    IReadOnlyList<SalePayment> Payments,
    bool ConfirmControlledDrugs,
    B2bBuyer? Buyer = null);

public sealed record PharmacySaleCreated(Guid Id, string InvoiceNumber, decimal NetAmount, decimal PaidAmount, decimal DueAmount, SaleStatus Status);

public sealed record PharmacySaleListItem(
    Guid Id,
    string InvoiceNumber,
    SaleType Type,
    DateTimeOffset SoldAt,
    Guid StoreId,
    Guid? PatientId,
    string? CustomerName,
    string? PrescriptionNumber,
    decimal NetAmount,
    decimal PaidAmount,
    decimal DueAmount,
    decimal ReturnedAmount,
    SaleStatus Status);

public sealed record PharmacySaleItemDto(
    Guid Id,
    Guid ItemId,
    string ItemName,
    string? GenericName,
    string? HsnCode,
    DrugSchedule Schedule,
    string BatchNumber,
    DateOnly? ExpiryDate,
    decimal Quantity,
    decimal UnitMrp,
    decimal GstRate,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal IgstAmount,
    decimal NetAmount,
    decimal ReturnedQuantity,
    bool IsSubstitution,
    string? PrescribedDrugName,
    string? SubstitutionReason);

public sealed record PharmacyPaymentDto(Guid Id, PaymentMode Mode, decimal Amount, string? Reference, DateTimeOffset ReceivedAt, bool IsRefund);

/// <summary>Everything the GST invoice print needs (letterhead comes from the facility master).</summary>
public sealed record PharmacySaleDetail(
    Guid Id,
    string InvoiceNumber,
    SaleType Type,
    Guid StoreId,
    string StoreName,
    Guid? PatientId,
    string? PatientUhid,
    string? CustomerName,
    string? CustomerPhone,
    string? PrescriptionNumber,
    string? PrescriberName,
    string? PrescriberRegistrationNumber,
    DateTimeOffset SoldAt,
    string? SoldBy,
    string? CounsellingNotes,
    decimal DiscountPercent,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal IgstAmount,
    string? BuyerGstin,
    string? BuyerLegalName,
    string? PlaceOfSupply,
    decimal RoundOff,
    decimal NetAmount,
    decimal PaidAmount,
    decimal DueAmount,
    decimal ReturnedAmount,
    decimal RefundedAmount,
    SaleStatus Status,
    string? CancelReason,
    IReadOnlyList<PharmacySaleItemDto> Items,
    IReadOnlyList<PharmacyPaymentDto> Payments,
    uint Version,
    string? BuyerAddress = null,
    EInvoiceStamp? EInvoice = null);

public sealed record CollectPaymentRequest(PaymentMode Mode, decimal Amount, string? Reference);

/// <param name="Restock">Put the returned quantity back on the shelf (unopened and in date); damaged returns are not restocked.</param>
public sealed record SaleReturnLine(Guid SaleItemId, decimal Quantity, bool Restock);

public sealed record SaleReturnRequest(IReadOnlyList<SaleReturnLine> Lines, string Reason, PaymentMode RefundMode, string? RefundReference);

public sealed record SaleReturnCreated(Guid Id, string ReturnNumber, decimal RefundAmount, decimal AdjustedAgainstDue, decimal CashRefund);

public sealed record SaleReturnDto(
    Guid Id,
    string ReturnNumber,
    DateTimeOffset ReturnedAt,
    string Reason,
    decimal RefundAmount,
    decimal AdjustedAgainstDue,
    decimal CashRefund,
    PaymentMode RefundMode,
    IReadOnlyList<SaleReturnItemDto> Items);

public sealed record SaleReturnItemDto(Guid SaleItemId, string ItemName, string BatchNumber, decimal Quantity, decimal RefundAmount, bool Restocked);

/// <summary>Counter picker row: what the store can sell now, at which MRP, and whether a prescription is needed.</summary>
/// <param name="Barcode">The item's own barcode, so a scan can be checked as an exact match.</param>
/// <param name="Scanned">When the search was a scanned pharmacy tag: the batch it names, in this store.</param>
public sealed record PharmacyStockRow(
    Guid ItemId,
    string Code,
    string Name,
    string? GenericName,
    string? Strength,
    DrugSchedule Schedule,
    bool IsControlled,
    bool RequiresPrescription,
    bool IsColdChain,
    string BaseUnit,
    decimal Available,
    decimal? Mrp,
    DateOnly? NearestExpiry,
    string? Barcode = null,
    ScannedBatch? Scanned = null);

/// <summary>
/// The batch a scanned pharmacy tag names (INT-BAR). <see cref="BatchId"/> is the store's batch to hand out, or null
/// with <see cref="Problem"/> saying why it cannot be: not in this store, none left, expired or blocked.
/// </summary>
public sealed record ScannedBatch(string BatchNumber, Guid? BatchId, DateOnly? ExpiryDate, decimal? Mrp, decimal Available, string? Problem);

public sealed class CounterSaleRequestValidator : AbstractValidator<CounterSaleRequest>
{
    public CounterSaleRequestValidator()
    {
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.CustomerName).SafeText(200);
        RuleFor(x => x.CustomerPhone).PhoneNumber();
        RuleFor(x => x.Prescription!.PrescriberName).NotEmpty().SafeText(200).When(x => x.Prescription is not null);
        RuleFor(x => x.Prescription!.PrescriberRegistrationNumber).MaximumLength(64).When(x => x.Prescription is not null);
        RuleFor(x => x.Prescription!.Reference).MaximumLength(64).When(x => x.Prescription is not null);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one item.").Must(l => l.Count <= 100);
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
        RuleFor(x => x.DiscountPercent).InclusiveBetween(0, 100);
        RuleFor(x => x.Payments).Must(p => p.Count <= 6).WithMessage("Split the bill into at most 6 payments.");
        RuleForEach(x => x.Payments).SetValidator(new SalePaymentValidator());
        RuleFor(x => x.Buyer!.Gstin).Must(Gstin.IsValid).WithMessage("Check the buyer's GSTIN: it is not a valid number.").When(x => x.Buyer is not null);
        RuleFor(x => x.Buyer!.LegalName).NotEmpty().SafeText(200).When(x => x.Buyer is not null);
        RuleFor(x => x.Buyer!.Address).SafeText(300).When(x => x.Buyer?.Address is not null);
        RuleFor(x => x.Buyer!.DeliveryState).Must(s => s is null || Gstin.States.ContainsKey(s)).WithMessage("Choose the state the goods are delivered to.")
            .When(x => x.Buyer is not null);
    }
}

public sealed class CollectPaymentRequestValidator : AbstractValidator<CollectPaymentRequest>
{
    public CollectPaymentRequestValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0).PrecisionScale(18, 2, true).WithMessage("Use at most 2 decimal places.");
        RuleFor(x => x.Mode).NotEqual(PaymentMode.Credit).WithMessage("Collect the balance in cash, card or UPI.");
        RuleFor(x => x.Reference).MaximumLength(64);
    }
}

public sealed class SaleReturnRequestValidator : AbstractValidator<SaleReturnRequest>
{
    public SaleReturnRequestValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Choose what is being returned.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.SaleItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
        RuleFor(x => x.Reason).NotEmpty().SafeText(300);
        RuleFor(x => x.RefundReference).MaximumLength(64);
    }
}
