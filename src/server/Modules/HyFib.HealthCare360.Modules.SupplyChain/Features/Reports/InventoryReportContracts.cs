using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Reports;

public sealed record ExpiryReport(DateOnly AsOf, int WithinDays, int BatchCount, decimal ValueAtCost, decimal ValueAtMrp, IReadOnlyList<ExpiringBatch> Batches);

/// <summary>Reorder suggestion: available + on order at or below the reorder level.</summary>
public sealed record ReorderRow(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    ItemType Type,
    VedClass VedClass,
    string BaseUnit,
    string PurchaseUnit,
    decimal PurchaseUnitFactor,
    decimal Available,
    decimal OnOrder,
    decimal ReorderLevel,
    decimal ReorderQuantity,
    decimal MaximumStock,
    decimal SuggestedQuantity,
    decimal SuggestedPurchaseUnits,
    decimal? LastUnitCost);

public sealed record ValuationRow(
    Guid StoreId,
    string StoreName,
    Guid ItemId,
    string ItemCode,
    string ItemName,
    ItemType Type,
    decimal Quantity,
    decimal ValueAtCost,
    decimal ValueAtMrp);

public sealed record ValuationByType(ItemType Type, decimal ValueAtCost, decimal ValueAtMrp);

public sealed record ValuationReport(DateOnly AsOf, decimal TotalAtCost, decimal TotalAtMrp, IReadOnlyList<ValuationByType> ByType, IReadOnlyList<ValuationRow> Rows);

/// <summary>One item in the ABC / VED / FSN analysis for a period.</summary>
public sealed record AnalysisRow(
    Guid ItemId,
    string ItemCode,
    string ItemName,
    ItemType Type,
    decimal ConsumedQuantity,
    decimal ConsumedValue,
    int IssueCount,
    decimal SharePercent,
    decimal CumulativePercent,
    AbcClass Abc,
    VedClass Ved,
    FsnClass Fsn,
    AbcVedCategory AbcVedCategory);

public sealed record ClassSummary(string Class, int Items, decimal Value, decimal ValuePercent);

public sealed record InventoryAnalysis(
    DateOnly From,
    DateOnly To,
    Guid? StoreId,
    decimal TotalValue,
    IReadOnlyList<ClassSummary> Abc,
    IReadOnlyList<ClassSummary> Fsn,
    IReadOnlyList<ClassSummary> AbcVed,
    IReadOnlyList<AnalysisRow> Items);

/// <summary>Store manager's dashboard (HMS-02): money in stock, expiry exposure and pending work.</summary>
public sealed record InventorySummary(
    decimal StockValueAtCost,
    decimal StockValueAtMrp,
    int NearExpiryBatches,
    decimal NearExpiryValue,
    int ExpiredBatches,
    decimal ExpiredValue,
    int ItemsBelowReorder,
    int PendingIndents,
    int PendingAdjustments,
    int OpenPurchaseOrders,
    int TransfersInTransit);
