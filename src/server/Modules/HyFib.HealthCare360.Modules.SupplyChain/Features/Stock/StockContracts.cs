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
    bool BelowReorder);

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

public sealed record BlockBatchRequest(string Reason);

public sealed class BlockBatchRequestValidator : AbstractValidator<BlockBatchRequest>
{
    public BlockBatchRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().SafeText(300).WithMessage("Give a reason (recall notice, quality hold…).");
}
