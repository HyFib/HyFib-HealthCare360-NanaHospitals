using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.VendorAccounts;

public sealed record DebitNoteLineInput(Guid BatchId, decimal Quantity);

/// <param name="GoodsReceiptId">The goods receipt the stock came in on, when known.</param>
/// <param name="VendorReference">The supplier's credit note or return authorisation number.</param>
public sealed record RaiseDebitNoteRequest(
    Guid VendorId, Guid StoreId, Guid? GoodsReceiptId, string? VendorReference, string Reason, IReadOnlyList<DebitNoteLineInput> Lines);

public sealed class RaiseDebitNoteRequestValidator : AbstractValidator<RaiseDebitNoteRequest>
{
    public RaiseDebitNoteRequestValidator()
    {
        RuleFor(x => x.VendorId).NotEmpty();
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.VendorReference).SafeText(60);
        RuleFor(x => x.Reason).NotEmpty().SafeText(500).WithMessage("Say why the goods go back (recall, damaged, short expiry, wrong supply).");
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one batch.")
            .Must(l => l.Count <= 200).WithMessage("A debit note can have at most 200 lines.")
            .Must(l => l.Select(x => x.BatchId).Distinct().Count() == l.Count).WithMessage("Add each batch once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.BatchId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
    }
}

public sealed record RaisePayableAdjustmentRequest(Guid VendorId, PayableAdjustmentDirection Direction, decimal Amount, string Reason, string? VendorReference);

public sealed class RaisePayableAdjustmentRequestValidator : AbstractValidator<RaisePayableAdjustmentRequest>
{
    public RaisePayableAdjustmentRequestValidator()
    {
        RuleFor(x => x.VendorId).NotEmpty();
        RuleFor(x => x.Direction).IsInEnum();
        RuleFor(x => x.Amount).GreaterThan(0).LessThanOrEqualTo(100_000_000);
        RuleFor(x => x.Reason).NotEmpty().SafeText(500);
        RuleFor(x => x.VendorReference).SafeText(60);
    }
}

public sealed record DebitNoteLineDto(Guid Id, Guid BatchId, Guid ItemId, string ItemName, string BatchNumber, DateOnly? ExpiryDate, decimal Quantity,
    decimal UnitCost, decimal GstRate, decimal TaxableAmount, decimal GstAmount);

public sealed record DebitNoteDto(
    Guid Id,
    string Number,
    Guid VendorId,
    string VendorName,
    Guid StoreId,
    string StoreName,
    Guid? GoodsReceiptId,
    string? VendorReference,
    string Reason,
    VendorDocumentStatus Status,
    DateTimeOffset RaisedAt,
    DateTimeOffset? PostedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    decimal TaxableAmount,
    decimal GstAmount,
    decimal TotalAmount,
    DateTimeOffset? ApprovalWaitingSince,
    IReadOnlyList<DebitNoteLineDto> Lines,
    uint Version);

public sealed record PayableAdjustmentDto(
    Guid Id,
    string Number,
    Guid VendorId,
    string VendorName,
    PayableAdjustmentDirection Direction,
    decimal Amount,
    string Reason,
    string? VendorReference,
    VendorDocumentStatus Status,
    DateTimeOffset RaisedAt,
    DateTimeOffset? PostedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    DateTimeOffset? ApprovalWaitingSince,
    uint Version);
