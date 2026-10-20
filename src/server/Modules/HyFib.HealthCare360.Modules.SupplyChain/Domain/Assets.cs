namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum AssetCategory
{
    MedicalEquipment = 0,
    IT,
    Furniture,
    Vehicle,
    Other,
}

public enum AssetStatus
{
    InUse = 0,
    UnderMaintenance,
    Idle,
    Condemned,
    Disposed,
}

/// <summary>Impact of the asset failing; drives ticket priority and PM discipline (ventilators are Critical).</summary>
public enum AssetCriticality
{
    Low = 0,
    Medium,
    High,
    Critical,
}

public enum DepreciationMethod
{
    StraightLine = 0,
    WrittenDownValue,
}

/// <summary>Annual Maintenance Contract (labour/visits) or Comprehensive Maintenance Contract (includes parts).</summary>
public enum MaintenanceContractType
{
    Amc = 0,
    Cmc,
}

/// <summary>
/// Fixed asset register entry (AST-01): tag/barcode, location, custodian, purchase and warranty details and the
/// depreciation basis. Status changes go through methods so that breakdowns, condemnation and disposal follow
/// the hospital's asset life cycle.
/// </summary>
public sealed class Asset : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>Printed tag "{FAC}AS{SEQ:6}", also encoded in the barcode label.</summary>
    public string AssetTag { get; set; } = string.Empty;

    public string? Barcode { get; set; }
    public string Name { get; set; } = string.Empty;
    public AssetCategory Category { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? Location { get; set; }
    public Guid? CustodianUserId { get; set; }
    public string? CustodianName { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public decimal PurchaseCost { get; set; }
    public Guid? VendorId { get; set; }
    public string? PurchaseOrderNumber { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateOnly? InstallationDate { get; set; }
    public DateOnly? WarrantyExpiry { get; set; }
    public AssetStatus Status { get; private set; }
    public AssetCriticality Criticality { get; set; }
    public DepreciationMethod DepreciationMethod { get; set; }
    public int UsefulLifeYears { get; set; } = 10;
    public decimal SalvageValue { get; set; }

    /// <summary>Annual rate for the written-down-value method (e.g. 15% for plant and machinery).</summary>
    public decimal DepreciationRatePercent { get; set; }

    public bool RequiresCalibration { get; set; }
    public DateOnly? NextCalibrationDue { get; set; }
    public DateOnly? CondemnedOn { get; private set; }
    public DateOnly? DisposedOn { get; private set; }
    public decimal? DisposalValue { get; private set; }
    public string? DisposalMethod { get; private set; }
    public string? Notes { get; set; }

    public bool IsInService => Status is not (AssetStatus.Condemned or AssetStatus.Disposed);

    /// <summary>Depreciation starts when the asset is put to use (installation), else on purchase.</summary>
    public DateOnly? DepreciationStart => InstallationDate ?? PurchaseDate;

    public Result MarkUnderMaintenance()
    {
        if (!IsInService)
        {
            return Error.Rule("asset.retired", $"Asset {AssetTag} is {Status}.");
        }

        Status = AssetStatus.UnderMaintenance;
        return Result.Success();
    }

    public Result ReturnToService()
    {
        if (!IsInService)
        {
            return Error.Rule("asset.retired", $"Asset {AssetTag} is {Status}.");
        }

        Status = AssetStatus.InUse;
        return Result.Success();
    }

    /// <summary>
    /// Applies a calibration certificate (AST-05). Only the latest certificate counts: it sets the next calibration date,
    /// and a failed one takes the equipment out of use. A certificate older than the latest on file (entered late) joins
    /// the history but changes neither, so it cannot move the due date back or retire equipment that has passed since.
    /// </summary>
    /// <param name="latestOnFile">Date of the latest certificate already recorded for this asset, if any.</param>
    public Result RecordCalibration(CalibrationRecord certificate, DateOnly? latestOnFile)
    {
        if (latestOnFile is { } latest && certificate.CalibratedOn < latest)
        {
            return Result.Success();
        }

        NextCalibrationDue = certificate.NextDueDate;
        RequiresCalibration = true;
        return certificate.Outcome == CalibrationResult.Fail && IsInService ? MarkUnderMaintenance() : Result.Success();
    }

    /// <summary>Moves between InUse and Idle (parked equipment); maintenance and retirement use their own flows.</summary>
    public Result SetOperational(bool inUse)
    {
        if (Status is not (AssetStatus.InUse or AssetStatus.Idle))
        {
            return Error.Rule("asset.status_locked", $"Asset {AssetTag} is {Status}; close its maintenance or condemnation first.");
        }

        Status = inUse ? AssetStatus.InUse : AssetStatus.Idle;
        return Result.Success();
    }

    public Result Condemn(DateOnly on)
    {
        if (!IsInService)
        {
            return Error.Rule("asset.retired", $"Asset {AssetTag} is already {Status}.");
        }

        Status = AssetStatus.Condemned;
        CondemnedOn = on;
        return Result.Success();
    }

    public Result Dispose(DateOnly on, decimal value, string method)
    {
        if (Status != AssetStatus.Condemned)
        {
            return Error.Rule("asset.not_condemned", "Only condemned assets can be disposed of.");
        }

        if (value < 0)
        {
            return Error.Field("disposalValue", "Disposal value cannot be negative.");
        }

        Status = AssetStatus.Disposed;
        DisposedOn = on;
        DisposalValue = value;
        DisposalMethod = method;
        return Result.Success();
    }
}

/// <summary>AMC/CMC contract for an asset (AST-02). Successive renewals are separate rows.</summary>
public sealed class AssetContract : AuditableEntity
{
    public Guid AssetId { get; set; }
    public MaintenanceContractType Type { get; set; }
    public Guid? VendorId { get; set; }
    public string? ContractNumber { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public decimal Cost { get; set; }
    public string? CoverageNotes { get; set; }

    public bool IsCurrent(DateOnly today) => StartDate <= today && today <= EndDate;
}
