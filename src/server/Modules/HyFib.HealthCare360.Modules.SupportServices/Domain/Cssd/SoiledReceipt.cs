namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

/// <summary>
/// Used trays received back from a department for reprocessing (CSSD-03). Every tray is counted against its count sheet
/// at the counter, so a missing instrument is caught (and its department told) before the tray is washed.
/// </summary>
public sealed class SoiledReceipt : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid DepartmentId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string? ReceivedByName { get; set; }
    public string? HandedOverByName { get; set; }
    public string? Notes { get; set; }
    public bool HasDiscrepancy { get; set; }
    public List<SoiledReceiptLine> Lines { get; set; } = [];
}

/// <summary>One tray on a soiled receipt with the result of its count check.</summary>
public sealed class SoiledReceiptLine : Entity
{
    public Guid SoiledReceiptId { get; set; }
    public Guid InstrumentSetId { get; set; }

    /// <summary>The issue (sterile pack) that came back, when known — closes the pack's traceability loop.</summary>
    public Guid? SetIssueId { get; set; }

    public int ExpectedCount { get; set; }
    public int ReceivedCount { get; set; }
    public int MissingCount { get; set; }
    public int ExtraCount { get; set; }
    public int DamagedCount { get; set; }
    public string? Discrepancies { get; set; }

    /// <summary>Per-instrument counts (JSON) as recorded at the counter.</summary>
    public string CountDetails { get; set; } = "[]";
}
