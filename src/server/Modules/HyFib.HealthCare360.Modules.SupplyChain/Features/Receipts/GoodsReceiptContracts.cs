using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;

/// <param name="Quantity">Invoiced quantity in purchase units.</param>
/// <param name="FreeQuantity">Free (scheme) quantity in purchase units.</param>
/// <param name="Rate">Rate per purchase unit excluding GST.</param>
/// <param name="Mrp">MRP printed on one purchase unit.</param>
/// <param name="AcceptShortExpiry">Confirms receipt of goods expiring within 90 days.</param>
public sealed record GoodsReceiptLineInput(
    Guid ItemId,
    Guid? PurchaseOrderLineId,
    string? BatchNumber,
    DateOnly? ManufactureDate,
    DateOnly? ExpiryDate,
    decimal Quantity,
    decimal FreeQuantity,
    decimal Rate,
    decimal Mrp,
    decimal DiscountPercent,
    decimal? GstRate,
    bool AcceptShortExpiry);

/// <param name="Post">Post stock immediately; otherwise the GRN stays a draft for verification.</param>
public sealed record CreateGoodsReceiptRequest(
    Guid VendorId,
    Guid StoreId,
    Guid? PurchaseOrderId,
    string VendorInvoiceNumber,
    DateOnly? VendorInvoiceDate,
    DateOnly? ReceivedOn,
    string? Remarks,
    IReadOnlyList<GoodsReceiptLineInput> Lines,
    bool Post);

public sealed record GoodsReceiptCreated(Guid Id, string Number, GoodsReceiptStatus Status, decimal NetAmount);

public sealed record GoodsReceiptListItem(
    Guid Id,
    string Number,
    Guid VendorId,
    string VendorName,
    Guid StoreId,
    string StoreName,
    string VendorInvoiceNumber,
    DateOnly? VendorInvoiceDate,
    DateOnly ReceivedOn,
    GoodsReceiptStatus Status,
    decimal NetAmount,
    Guid? PurchaseOrderId);

public sealed record GoodsReceiptLineDto(
    Guid Id,
    Guid ItemId,
    string ItemName,
    Guid? PurchaseOrderLineId,
    string BatchNumber,
    DateOnly? ManufactureDate,
    DateOnly? ExpiryDate,
    decimal Quantity,
    decimal FreeQuantity,
    decimal Rate,
    decimal Mrp,
    decimal DiscountPercent,
    decimal GstRate,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal IgstAmount,
    decimal LineTotal,
    decimal BaseQuantity,
    decimal UnitCost,
    decimal UnitMrp,
    Guid? StockBatchId,
    string? ItemCode = null,
    string? BaseUnit = null);

public sealed record GoodsReceiptDetail(
    Guid Id,
    string Number,
    Guid VendorId,
    string VendorName,
    string? VendorGstin,
    Guid StoreId,
    string StoreName,
    Guid? PurchaseOrderId,
    string VendorInvoiceNumber,
    DateOnly? VendorInvoiceDate,
    DateOnly ReceivedOn,
    bool IsInterState,
    GoodsReceiptStatus Status,
    string? Remarks,
    DateTimeOffset? PostedAt,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal IgstAmount,
    decimal RoundOff,
    decimal NetAmount,
    IReadOnlyList<GoodsReceiptLineDto> Lines,
    uint Version);

public sealed class CreateGoodsReceiptRequestValidator : AbstractValidator<CreateGoodsReceiptRequest>
{
    public CreateGoodsReceiptRequestValidator()
    {
        RuleFor(x => x.VendorId).NotEmpty();
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.VendorInvoiceNumber).NotEmpty().MaximumLength(40).WithMessage("Enter the vendor's invoice number.");
        RuleFor(x => x.Remarks).SafeText(1000);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one received item.")
            .Must(l => l.Count <= 300).WithMessage("A GRN can have at most 300 lines.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.BatchNumber).MaximumLength(40);
            line.RuleFor(l => l.Quantity).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.FreeQuantity).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l).Must(l => l.Quantity + l.FreeQuantity > 0).WithName("quantity").WithMessage("Enter the received quantity.");
            line.RuleFor(l => l.Rate).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.Mrp).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.DiscountPercent).InclusiveBetween(0, 100);
            line.RuleFor(l => l.GstRate).Must(r => r is null || GstCalculator.IsStandardRate(r.Value)).WithMessage("Use a notified GST rate.");
        });
    }
}
