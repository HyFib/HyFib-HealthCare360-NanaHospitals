using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;

/// <summary>Reason for a reject / cancel / close action anywhere in inventory and purchase.</summary>
public sealed record InventoryReasonRequest(string Reason);

public sealed record RequisitionLineInput(Guid ItemId, decimal Quantity, string? Remarks);

public sealed record CreateRequisitionRequest(Guid StoreId, DateOnly? RequiredBy, string? Remarks, IReadOnlyList<RequisitionLineInput> Lines);

public sealed record RequisitionListItem(
    Guid Id,
    string Number,
    Guid StoreId,
    string StoreName,
    RequisitionStatus Status,
    DateOnly? RequiredBy,
    int LineCount,
    DateTimeOffset CreatedAt,
    string? RequestedBy);

public sealed record RequisitionLineDto(Guid Id, Guid ItemId, string ItemName, decimal Quantity, string? Remarks);

public sealed record RequisitionDetail(
    Guid Id,
    string Number,
    Guid StoreId,
    string StoreName,
    RequisitionStatus Status,
    DateOnly? RequiredBy,
    string? Remarks,
    string? RejectReason,
    Guid? PurchaseOrderId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    IReadOnlyList<RequisitionLineDto> Lines,
    uint Version);

/// <param name="Quantity">Purchase units (strips, boxes).</param>
/// <param name="GstRate">Defaults to the item master's GST rate.</param>
public sealed record PurchaseOrderLineInput(Guid ItemId, decimal Quantity, decimal FreeQuantity, decimal Rate, decimal DiscountPercent, decimal? GstRate);

public sealed record SavePurchaseOrderRequest(
    Guid VendorId,
    Guid StoreId,
    Guid? RequisitionId,
    DateOnly? ExpectedDeliveryDate,
    int? PaymentTermsDays,
    string? Remarks,
    IReadOnlyList<PurchaseOrderLineInput> Lines,
    uint? Version);

public sealed record PurchaseOrderListItem(
    Guid Id,
    string Number,
    Guid VendorId,
    string VendorName,
    Guid StoreId,
    string StoreName,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
    PurchaseOrderStatus Status,
    decimal NetAmount,
    int LineCount);

public sealed record PurchaseOrderLineDto(
    Guid Id,
    Guid ItemId,
    string ItemName,
    string PurchaseUnit,
    decimal Quantity,
    decimal FreeQuantity,
    decimal Rate,
    decimal DiscountPercent,
    decimal GstRate,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal IgstAmount,
    decimal LineTotal,
    decimal ReceivedQuantity,
    decimal PendingQuantity);

public sealed record PurchaseOrderDetail(
    Guid Id,
    string Number,
    Guid VendorId,
    string VendorName,
    string? VendorGstin,
    Guid StoreId,
    string StoreName,
    Guid? RequisitionId,
    DateOnly OrderDate,
    DateOnly? ExpectedDeliveryDate,
    bool IsInterState,
    int PaymentTermsDays,
    string? Remarks,
    PurchaseOrderStatus Status,
    string? ReviewNote,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal IgstAmount,
    decimal RoundOff,
    decimal NetAmount,
    IReadOnlyList<PurchaseOrderLineDto> Lines,
    uint Version);

public sealed record DocumentCreated(Guid Id, string Number);

public sealed class InventoryReasonRequestValidator : AbstractValidator<InventoryReasonRequest>
{
    public InventoryReasonRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class CreateRequisitionRequestValidator : AbstractValidator<CreateRequisitionRequest>
{
    public CreateRequisitionRequestValidator()
    {
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.Remarks).SafeText(1000);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one item.")
            .Must(l => l.Count <= 300).WithMessage("A requisition can have at most 300 lines.")
            .Must(l => l.Select(x => x.ItemId).Distinct().Count() == l.Count).WithMessage("Each item can appear only once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
            line.RuleFor(l => l.Remarks).SafeText(300);
        });
    }
}

public sealed class SavePurchaseOrderRequestValidator : AbstractValidator<SavePurchaseOrderRequest>
{
    public SavePurchaseOrderRequestValidator()
    {
        RuleFor(x => x.VendorId).NotEmpty();
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.PaymentTermsDays).InclusiveBetween(0, 365);
        RuleFor(x => x.Remarks).SafeText(1000);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one item.")
            .Must(l => l.Count <= 300).WithMessage("An order can have at most 300 lines.")
            .Must(l => l.Select(x => x.ItemId).Distinct().Count() == l.Count).WithMessage("Each item can appear only once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
            line.RuleFor(l => l.FreeQuantity).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.Rate).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.DiscountPercent).InclusiveBetween(0, 100);
            line.RuleFor(l => l.GstRate).Must(r => r is null || GstCalculator.IsStandardRate(r.Value)).WithMessage("Use a notified GST rate.");
        });
    }
}
