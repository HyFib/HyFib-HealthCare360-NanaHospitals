namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>A suggestion to reorder one item; quantities are in its base unit, except <see cref="PurchaseUnits"/>.</summary>
/// <param name="Available">Usable stock: on hand less reserved, in batches neither blocked nor expired.</param>
/// <param name="OnOrder">Still to come on open purchase orders.</param>
/// <param name="Quantity">What to order: up to the maximum stock, and never less than the reorder quantity.</param>
/// <param name="PurchaseUnits">The quantity in whole purchase units (strips, boxes), rounded up.</param>
public sealed record ReorderSuggestion(Item Item, decimal Available, decimal OnOrder, decimal Quantity, decimal PurchaseUnits);

/// <summary>
/// The reorder rule (INV-08, PH-04). An active item with a reorder level is due when its usable stock plus what is still
/// on order is at or under that level. The suggestion tops the stock up to the item's maximum (or, with no maximum set,
/// to the reorder level plus the reorder quantity), never orders less than the reorder quantity, and leaves out what is
/// already on order, so an open purchase order is never ordered twice. Vital items come first, then essential, then
/// desirable, each by name.
/// </summary>
public static class ReorderCalculator
{
    /// <summary>A requisition made from the reorder report takes at most this many items, the most critical first.</summary>
    public const int MaxRequisitionLines = 300;

    /// <param name="item">The item, with its reorder level, reorder quantity, maximum stock and purchase unit.</param>
    /// <param name="available">Usable stock, in base units.</param>
    /// <param name="pendingPurchaseUnits">Ordered and not yet received on approved, sent or part-received orders, in purchase units.</param>
    /// <returns>The suggestion, or <c>null</c> when the item is not due.</returns>
    public static ReorderSuggestion? Suggest(Item item, decimal available, decimal pendingPurchaseUnits)
    {
        if (!item.IsActive || item.ReorderLevel <= 0)
        {
            return null;
        }

        var onOrder = Math.Max(pendingPurchaseUnits, 0) * item.PurchaseUnitFactor;
        if (available + onOrder > item.ReorderLevel)
        {
            return null;
        }

        var target = item.MaximumStock > 0 ? item.MaximumStock : item.ReorderLevel + item.ReorderQuantity;
        var quantity = Math.Max(item.ReorderQuantity, target - available - onOrder);
        var purchaseUnits = item.PurchaseUnitFactor <= 0 ? quantity : Math.Ceiling(quantity / item.PurchaseUnitFactor);
        return new ReorderSuggestion(item, available, onOrder, quantity, purchaseUnits);
    }

    /// <summary>The order the store buys in: vital items first, then essential, then desirable, each by name.</summary>
    public static IReadOnlyList<ReorderSuggestion> Rank(IEnumerable<ReorderSuggestion> suggestions) =>
        suggestions
            .OrderBy(s => s.Item.VedClass switch { VedClass.Vital => 0, VedClass.Essential => 1, _ => 2 })
            .ThenBy(s => s.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Purchase units a requisition line made from a suggestion asks for: the suggestion, and at least one.</summary>
    public static decimal RequisitionQuantity(decimal suggestedPurchaseUnits) => Math.Max(suggestedPurchaseUnits, 1);
}
