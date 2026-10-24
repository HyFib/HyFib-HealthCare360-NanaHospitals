using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

/// <summary>
/// Statutory register line for a Schedule H1 / X / NDPS-controlled item (PH-06): every receipt, transfer,
/// dispense, return and write-off with the store's running balance of that item. Dispenses record the patient,
/// prescriber and prescription. Append-only (one row per stock-ledger movement): a database trigger refuses a delete and
/// any change but the patient link a merge of duplicate records moves (migration <c>ControlledDrugRegisterAppendOnly</c>).
/// Unlike the stock ledger it is audited (<see cref="AuditedAttribute"/>): each line, and each move of its patient link,
/// has a copy in the sealed audit chain, so a line changed behind the server's back no longer matches it.
/// </summary>
[Audited]
public sealed class ControlledDrugRegisterEntry : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid StoreId { get; set; }
    public Guid ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public DrugSchedule Schedule { get; set; }
    public bool IsControlled { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }
    public StockMovementType MovementType { get; set; }
    public decimal QuantityIn { get; set; }
    public decimal QuantityOut { get; set; }

    /// <summary>Balance of the item in the store after this movement (exact: stock movements are serialised per item and store).</summary>
    public decimal BalanceAfter { get; set; }

    public Guid? PatientId { get; set; }
    public string? PatientName { get; set; }
    public string? PatientUhid { get; set; }
    public string? PatientAddress { get; set; }
    public string? PrescriberName { get; set; }
    public string? PrescriberRegistrationNumber { get; set; }
    public string? PrescriptionNumber { get; set; }
    public string ReferenceModule { get; set; } = string.Empty;
    public Guid? ReferenceId { get; set; }
    public string? ReferenceNumber { get; set; }
    public Guid LedgerEntryId { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? Remarks { get; set; }
}
