namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum ReservationStatus
{
    Active = 0,
    Consumed,
    Released,
}

/// <summary>
/// Stock set aside in one batch for a document (approved indent, booked surgery implant…). The batch's
/// <see cref="StockBatch.QuantityReserved"/> is the fast total; these rows say who holds it so it can be released
/// or consumed precisely when the document is issued, cancelled or closed.
/// </summary>
[NotAudited]
public sealed class StockReservation : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ItemId { get; set; }
    public Guid BatchId { get; set; }
    public decimal Quantity { get; set; }
    public decimal ConsumedQuantity { get; private set; }
    public decimal ReleasedQuantity { get; private set; }
    public string ReferenceModule { get; set; } = string.Empty;
    public Guid ReferenceId { get; set; }
    public Guid? ReferenceLineId { get; set; }
    public string? ReferenceNumber { get; set; }
    public ReservationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public uint Version { get; set; }

    public decimal Outstanding => Quantity - ConsumedQuantity - ReleasedQuantity;

    public Result Consume(decimal quantity, DateTimeOffset now)
    {
        if (Status != ReservationStatus.Active)
        {
            return Error.Rule("reservation.closed", "This reservation is no longer active.");
        }

        if (quantity <= 0 || quantity > Outstanding)
        {
            return Error.Rule("reservation.exceeded", $"Only {StockErrors.Format(Outstanding)} is reserved.");
        }

        ConsumedQuantity += quantity;
        CloseIfDone(now, ReservationStatus.Consumed);
        return Result.Success();
    }

    /// <summary>Releases whatever is still outstanding and returns that quantity (0 if already closed).</summary>
    public decimal Release(DateTimeOffset now)
    {
        if (Status != ReservationStatus.Active)
        {
            return 0;
        }

        var released = Outstanding;
        ReleasedQuantity += released;
        CloseIfDone(now, ConsumedQuantity > 0 ? ReservationStatus.Consumed : ReservationStatus.Released);
        return released;
    }

    private void CloseIfDone(DateTimeOffset now, ReservationStatus closedAs)
    {
        if (Outstanding > 0)
        {
            return;
        }

        Status = closedAs;
        ClosedAt = now;
    }
}
