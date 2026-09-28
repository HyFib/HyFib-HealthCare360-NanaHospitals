namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>Why stock moved. Receipts are positive ledger quantities, issues negative.</summary>
public enum StockMovementType
{
    Grn = 0,
    Issue,
    TransferIn,
    TransferOut,
    Return,
    Adjustment,
    Sale,
    Dispense,
    Consumption,
    ExpiryWriteOff,

    /// <summary>Opening balance loaded at go-live or by demo seeding.</summary>
    Opening,
}

/// <summary>
/// One stock movement (INV stock ledger). Append-only and high-volume, so it is not data-audited: the row itself
/// is the audit record (who, when, why, which document). <see cref="BalanceAfter"/> is the item's balance in the
/// store after the movement — exact because <c>StockService</c> serialises movements per item and store.
/// </summary>
[NotAudited]
public sealed class StockLedgerEntry : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid StoreId { get; set; }
    public Guid ItemId { get; set; }
    public Guid BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public StockMovementType MovementType { get; set; }

    /// <summary>Signed quantity in base units.</summary>
    public decimal Quantity { get; set; }

    public decimal BatchBalanceAfter { get; set; }

    /// <summary>Balance of the item in this store (all batches) after the movement — the stock-card balance.</summary>
    public decimal BalanceAfter { get; set; }

    public decimal UnitCost { get; set; }
    public decimal Mrp { get; set; }

    /// <summary>Module that caused the movement ("supplychain.grn", "pharmacy.dispense"…).</summary>
    public string ReferenceModule { get; set; } = string.Empty;

    public Guid? ReferenceId { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? Remarks { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>Other store of a transfer (source for transfer-in, destination for transfer-out).</summary>
    public Guid? CounterpartStoreId { get; set; }

    public decimal Value => Quantity * UnitCost;

    /// <summary>Consumption movements feed ABC/FSN analysis; transfers and write-offs do not.</summary>
    public static bool IsConsumption(StockMovementType type) =>
        type is StockMovementType.Issue or StockMovementType.Sale or StockMovementType.Dispense or StockMovementType.Consumption or StockMovementType.Return;

    /// <summary>Announces a movement of a register item (Schedule H1/X or NDPS) to modules that keep statutory registers.</summary>
    internal void AnnounceRegisterMovement() => Raise(new ControlledStockMoved(
        Id, FacilityId, StoreId, ItemId, BatchId, BatchNumber, ExpiryDate, MovementType, Quantity, BalanceAfter,
        ReferenceModule, ReferenceId, ReferenceNumber, Remarks, OccurredAt, UserId));
}

/// <summary>
/// A register item (Schedule H1/X or controlled) moved. The Pharmacy module records it in the controlled-drug
/// register so receipts, transfers and write-offs made outside pharmacy screens still appear with a balance.
/// </summary>
public sealed record ControlledStockMoved(
    Guid LedgerEntryId,
    Guid FacilityId,
    Guid StoreId,
    Guid ItemId,
    Guid BatchId,
    string BatchNumber,
    DateOnly? ExpiryDate,
    StockMovementType MovementType,
    decimal Quantity,
    decimal BalanceAfter,
    string ReferenceModule,
    Guid? ReferenceId,
    string? ReferenceNumber,
    string? Remarks,
    DateTimeOffset MovedAt,
    Guid? UserId) : DomainEvent;
