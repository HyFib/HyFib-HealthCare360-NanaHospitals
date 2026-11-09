namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>A suggestion to reorder one item; quantities are in its base unit, except <see cref="PurchaseUnits"/>.</summary>
/// <param name="Available">Usable stock: on hand less reserved, in batches neither blocked nor expired.</param>
/// <param name="OnOrder">Still to come on open purchase orders.</param>
/// <param name="Quantity">What to order: up to the maximum stock, and never less than the reorder quantity.</param>
/// <param name="PurchaseUnits">The quantity in whole purchase units (strips, boxes), rounded up.</param>
/// <param name="Levels">The levels it was worked out by: the store's own, or the item's.</param>
public sealed record ReorderSuggestion(Item Item, decimal Available, decimal OnOrder, decimal Quantity, decimal PurchaseUnits, ReorderLevels Levels);

/// <summary>An item's reorder level, reorder quantity, minimum and maximum stock, in its base unit.</summary>
public sealed record ReorderLevels(decimal ReorderLevel, decimal ReorderQuantity, decimal MinimumStock, decimal MaximumStock)
{
    public string Words(string unit)
    {
        static string Q(decimal q) => q.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return ReorderLevel <= 0
            ? "not reordered"
            : $"reorder at {Q(ReorderLevel)} {unit}, order at least {Q(ReorderQuantity)}, minimum {Q(MinimumStock)}, maximum {(MaximumStock > 0 ? Q(MaximumStock) : "not set")}";
    }
}

/// <summary>
/// A store's own reorder levels for an item, overriding the item master's (a ward sub-store keeps far less paracetamol than
/// the central store). Changes are audited with the levels before and after. They are made at once, without a second person:
/// a level only decides when the store is told to reorder, so a level set too low risks running short, which the store sees
/// on its own stock screen, and never lets stock, money or a patient's medicine out (MAKER-CHECKER slice 13).
/// </summary>
public sealed class StoreItemLevel : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ItemId { get; set; }
    public decimal ReorderLevel { get; set; }
    public decimal ReorderQuantity { get; set; }
    public decimal MinimumStock { get; set; }
    public decimal MaximumStock { get; set; }

    public ReorderLevels Levels => new(ReorderLevel, ReorderQuantity, MinimumStock, MaximumStock);

    public Result Set(ReorderLevels levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.ReorderLevel < 0 || levels.ReorderQuantity < 0 || levels.MinimumStock < 0 || levels.MaximumStock < 0)
        {
            return Error.Field("reorderLevel", "Levels cannot be negative.");
        }

        if (levels.MaximumStock > 0 && levels.MaximumStock < levels.ReorderLevel)
        {
            return Error.Field("maximumStock", "The maximum stock cannot be under the reorder level.");
        }

        ReorderLevel = levels.ReorderLevel;
        ReorderQuantity = levels.ReorderQuantity;
        MinimumStock = levels.MinimumStock;
        MaximumStock = levels.MaximumStock;
        return Result.Success();
    }
}

/// <summary>
/// The reorder rule (INV-08, PH-04). An active item with a reorder level is due when its usable stock plus what is still
/// on order is at or under that level. The suggestion tops the stock up to the item's maximum (or, with no maximum set,
/// to the reorder level plus the reorder quantity), never orders less than the reorder quantity, and leaves out what is
/// already on order, so an open purchase order is never ordered twice. Vital items come first, then essential, then
/// desirable, each by name.
/// </summary>
public static class ReorderCalculator
{
    /// <summary>The levels a store reorders an item by: the store's own (<see cref="StoreItemLevel"/>) or else the item's.</summary>
    public static ReorderLevels LevelsFor(Item item, StoreItemLevel? store)
    {
        ArgumentNullException.ThrowIfNull(item);
        return store is null
            ? new ReorderLevels(item.ReorderLevel, item.ReorderQuantity, item.MinimumStock, item.MaximumStock)
            : new ReorderLevels(store.ReorderLevel, store.ReorderQuantity, store.MinimumStock, store.MaximumStock);
    }

    /// <summary>A requisition made from the reorder report takes at most this many items, the most critical first.</summary>
    public const int MaxRequisitionLines = 300;

    /// <param name="item">The item, with its reorder level, reorder quantity, maximum stock and purchase unit.</param>
    /// <param name="available">Usable stock, in base units.</param>
    /// <param name="pendingPurchaseUnits">Ordered and not yet received on approved, sent or part-received orders, in purchase units.</param>
    /// <returns>The suggestion, or <c>null</c> when the item is not due.</returns>
    public static ReorderSuggestion? Suggest(Item item, decimal available, decimal pendingPurchaseUnits) =>
        Suggest(item, LevelsFor(item, null), available, pendingPurchaseUnits);

    /// <param name="levels">The levels to reorder by: a store's own levels override the item's (<see cref="LevelsFor"/>).</param>
    public static ReorderSuggestion? Suggest(Item item, ReorderLevels levels, decimal available, decimal pendingPurchaseUnits)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(levels);
        var onOrder = Math.Max(pendingPurchaseUnits, 0) * item.PurchaseUnitFactor;
        if (!IsDue(item.IsActive, levels.ReorderLevel, available, onOrder))
        {
            return null;
        }

        var target = levels.MaximumStock > 0 ? levels.MaximumStock : levels.ReorderLevel + levels.ReorderQuantity;
        var quantity = Math.Max(levels.ReorderQuantity, target - available - onOrder);
        var purchaseUnits = item.PurchaseUnitFactor <= 0 ? quantity : Math.Ceiling(quantity / item.PurchaseUnitFactor);
        return new ReorderSuggestion(item, available, onOrder, quantity, purchaseUnits, levels);
    }

    /// <summary>
    /// Whether an item is due for reorder: active, with a reorder level, and its usable stock plus what is on order (both in
    /// base units) at or under the level. The stock screen's flag and the reorder report use this one rule.
    /// </summary>
    public static bool IsDue(bool isActive, decimal reorderLevel, decimal available, decimal onOrderBaseUnits) =>
        isActive && reorderLevel > 0 && available + Math.Max(onOrderBaseUnits, 0) <= reorderLevel;

    /// <summary>The order the store buys in: vital items first, then essential, then desirable, each by name.</summary>
    public static IReadOnlyList<ReorderSuggestion> Rank(IEnumerable<ReorderSuggestion> suggestions) =>
        suggestions
            .OrderBy(s => s.Item.VedClass switch { VedClass.Vital => 0, VedClass.Essential => 1, _ => 2 })
            .ThenBy(s => s.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Purchase units a requisition line made from a suggestion asks for: the suggestion, and at least one.</summary>
    public static decimal RequisitionQuantity(decimal suggestedPurchaseUnits) => Math.Max(suggestedPurchaseUnits, 1);
}
