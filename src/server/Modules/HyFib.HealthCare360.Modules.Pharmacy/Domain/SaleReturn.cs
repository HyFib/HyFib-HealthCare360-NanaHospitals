namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

/// <summary>
/// Sales return against a pharmacy invoice (PH-02). Unopened, unexpired stock goes back into the batch it came
/// from; damaged or expired returns are recorded but not restocked. The refund first reduces any balance due.
/// </summary>
public sealed class PharmacySaleReturn : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string ReturnNumber { get; set; } = string.Empty;
    public Guid SaleId { get; set; }
    public Guid? PatientId { get; set; }
    public DateTimeOffset ReturnedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal RefundAmount { get; set; }
    public decimal AdjustedAgainstDue { get; set; }
    public decimal CashRefund { get; set; }
    public PaymentMode RefundMode { get; set; }
    public string? RefundReference { get; set; }
    public List<PharmacySaleReturnItem> Items { get; set; } = [];
}

public sealed class PharmacySaleReturnItem : Entity
{
    public Guid SaleReturnId { get; set; }
    public Guid SaleItemId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public Guid BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal RefundAmount { get; set; }
    public bool Restocked { get; set; }
}
