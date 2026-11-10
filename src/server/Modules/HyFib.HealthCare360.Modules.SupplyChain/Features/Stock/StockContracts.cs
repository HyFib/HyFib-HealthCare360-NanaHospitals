using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Stock;

/// <summary>Item position in one store (PH-04 "stock per pharmacy store").</summary>
public sealed record StockSummaryRow(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    ItemType Type,
    string BaseUnit,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    decimal ExpiredQuantity,
    DateOnly? NearestExpiry,
    decimal ValueAtCost,
    decimal ReorderLevel,
    bool BelowReorder,
    decimal BlockedQuantity = 0,
    decimal OnOrder = 0);

/// <summary>One stock-card line: movement in/out with the store balance after it.</summary>
public sealed record LedgerRow(
    Guid Id,
    DateTimeOffset OccurredAt,
    StockMovementType MovementType,
    Guid ItemId,
    string ItemName,
    Guid BatchId,
    string BatchNumber,
    DateOnly? ExpiryDate,
    decimal QuantityIn,
    decimal QuantityOut,
    decimal BalanceAfter,
    decimal BatchBalanceAfter,
    decimal UnitCost,
    string ReferenceModule,
    Guid? ReferenceId,
    string? ReferenceNumber,
    string? Remarks,
    string? UserName,
    Guid? CounterpartStoreId);

public sealed record ReservationRow(
    Guid Id,
    Guid ItemId,
    string ItemName,
    Guid BatchId,
    string BatchNumber,
    decimal Quantity,
    decimal Outstanding,
    string ReferenceModule,
    Guid ReferenceId,
    string? ReferenceNumber,
    DateTimeOffset CreatedAt);

/// <param name="Recall">A manufacturer's or regulator's recall (not a local quality hold): released only with a second person's approval.</param>
public sealed record BlockBatchRequest(string Reason, bool Recall = false);

public sealed class BlockBatchRequestValidator : AbstractValidator<BlockBatchRequest>
{
    public BlockBatchRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().SafeText(300).WithMessage("Give a reason (recall notice, quality hold…).");
}

/// <param name="Reason">Why the batch may be used again (the recall withdrawn, the batch cleared); needed to release a recall.</param>
public sealed record UnblockBatchRequest(string? Reason);

public sealed class UnblockBatchRequestValidator : AbstractValidator<UnblockBatchRequest>
{
    public UnblockBatchRequestValidator() => RuleFor(x => x.Reason).SafeText(300);
}
