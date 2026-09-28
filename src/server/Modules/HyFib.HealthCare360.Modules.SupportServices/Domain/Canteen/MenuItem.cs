namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;

public enum MenuCategory
{
    Breakfast = 0,
    Meals,
    Snacks,
    Beverages,
    Juices,
    Sweets,
    Packaged,
    Other,
}

/// <summary>
/// A canteen menu item (CAN-01). Prices are GST-inclusive by default, as displayed on canteen boards; restaurant service
/// without input tax credit attracts 5 % GST (2.5 % CGST + 2.5 % SGST). Packaged goods can track stock so daily
/// consumption and closing stock are reported (CAN-04).
/// </summary>
public sealed class MenuItem : AuditableEntity, IFacilityScoped
{
    public const decimal StandardGstRate = 5m;

    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public MenuCategory Category { get; set; }
    public decimal Price { get; set; }
    public decimal GstRate { get; set; } = StandardGstRate;
    public bool PriceIncludesGst { get; set; } = true;
    public bool IsVeg { get; set; } = true;
    public bool IsAvailable { get; set; } = true;
    public bool TrackStock { get; set; }
    public int StockOnHand { get; set; }
    public int DisplayOrder { get; set; }
    public string? Description { get; set; }

    public Result Consume(int quantity)
    {
        if (TrackStock && StockOnHand < quantity)
        {
            return Error.Rule("canteen.out_of_stock", $"Only {StockOnHand} {Name} left in stock.");
        }

        if (TrackStock)
        {
            StockOnHand -= quantity;
        }

        return Result.Success();
    }

    public void Restock(int quantity)
    {
        if (TrackStock)
        {
            StockOnHand += quantity;
        }
    }
}

public enum StockMovementKind
{
    Receipt = 0,
    Adjustment,
    Wastage,
}

/// <summary>Stock received, adjusted after a physical count or written off (sales reduce stock directly on the bill).</summary>
public sealed class CanteenStockMovement : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid MenuItemId { get; set; }
    public StockMovementKind Kind { get; set; }

    /// <summary>Signed change in units (receipts positive, wastage negative, adjustments either way).</summary>
    public int Quantity { get; set; }

    public int StockAfter { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset MovedAt { get; set; }
}
