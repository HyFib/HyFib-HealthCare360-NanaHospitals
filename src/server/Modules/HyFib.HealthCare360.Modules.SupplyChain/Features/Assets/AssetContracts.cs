using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Assets;

public sealed record SaveAssetRequest(
    string Name,
    AssetCategory Category,
    string? Make,
    string? Model,
    string? SerialNumber,
    Guid? DepartmentId,
    string? Location,
    Guid? CustodianUserId,
    string? CustodianName,
    DateOnly? PurchaseDate,
    decimal PurchaseCost,
    Guid? VendorId,
    string? PurchaseOrderNumber,
    string? InvoiceNumber,
    DateOnly? InstallationDate,
    DateOnly? WarrantyExpiry,
    AssetCriticality Criticality,
    DepreciationMethod DepreciationMethod,
    int UsefulLifeYears,
    decimal SalvageValue,
    decimal? DepreciationRatePercent,
    bool RequiresCalibration,
    string? Barcode,
    string? Notes,
    uint? Version);

public sealed record SetAssetOperationalRequest(bool InUse);

public sealed record AssetListItem(
    Guid Id,
    string AssetTag,
    string Name,
    AssetCategory Category,
    string? Make,
    string? Model,
    string? SerialNumber,
    Guid? DepartmentId,
    string? DepartmentName,
    string? Location,
    AssetStatus Status,
    AssetCriticality Criticality,
    DateOnly? WarrantyExpiry,
    DateOnly? NextPmDue,
    DateOnly? NextCalibrationDue,
    int OpenTickets);

public sealed record AssetDetail(
    Guid Id,
    string AssetTag,
    string? Barcode,
    string Name,
    AssetCategory Category,
    string? Make,
    string? Model,
    string? SerialNumber,
    Guid? DepartmentId,
    string? DepartmentName,
    string? Location,
    Guid? CustodianUserId,
    string? CustodianName,
    DateOnly? PurchaseDate,
    decimal PurchaseCost,
    Guid? VendorId,
    string? VendorName,
    string? PurchaseOrderNumber,
    string? InvoiceNumber,
    DateOnly? InstallationDate,
    DateOnly? WarrantyExpiry,
    bool UnderWarranty,
    AssetStatus Status,
    AssetCriticality Criticality,
    DepreciationMethod DepreciationMethod,
    int UsefulLifeYears,
    decimal SalvageValue,
    decimal DepreciationRatePercent,
    decimal BookValue,
    bool RequiresCalibration,
    DateOnly? NextCalibrationDue,
    DateOnly? CondemnedOn,
    DateOnly? DisposedOn,
    decimal? DisposalValue,
    string? DisposalMethod,
    string? Notes,
    uint Version);

public sealed record SaveAssetContractRequest(
    MaintenanceContractType Type,
    Guid? VendorId,
    string? ContractNumber,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal Cost,
    string? CoverageNotes,
    uint? Version);

public sealed record AssetContractDto(
    Guid Id,
    Guid AssetId,
    MaintenanceContractType Type,
    Guid? VendorId,
    string? VendorName,
    string? ContractNumber,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal Cost,
    string? CoverageNotes,
    bool IsCurrent,
    uint Version);

public sealed record AssetDepreciation(
    Guid AssetId,
    DepreciationMethod Method,
    decimal Cost,
    decimal SalvageValue,
    decimal RatePercent,
    DateOnly? StartDate,
    DateOnly AsOf,
    decimal BookValue,
    decimal AccumulatedDepreciation,
    IReadOnlyList<DepreciationYear> Schedule);

public sealed record RequestCondemnationRequest(string Reason);

public sealed record DecideCondemnationRequest(string? Remarks);

public sealed record DisposeAssetRequest(DateOnly DisposedOn, decimal DisposalValue, string Method);

public sealed record CondemnationDto(
    Guid Id,
    Guid AssetId,
    string AssetTag,
    string AssetName,
    string Reason,
    decimal BookValueAtRequest,
    CondemnationStatus Status,
    DateTimeOffset RequestedAt,
    string? RequestedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionRemarks);

public sealed record AssetStatusCount(AssetStatus Status, int Count);

/// <summary>Biomedical engineering dashboard (AST): fleet status, open breakdowns and what falls due.</summary>
public sealed record AssetDashboard(
    int TotalAssets,
    IReadOnlyList<AssetStatusCount> ByStatus,
    int OpenTickets,
    int CriticalOpenTickets,
    int PmDueThisWeek,
    int PmOverdue,
    int CalibrationDue,
    int WarrantyExpiring,
    int ContractsExpiring,
    decimal DowntimeHoursThisMonth,
    decimal BookValue);

public sealed class SaveAssetRequestValidator : AbstractValidator<SaveAssetRequest>
{
    public SaveAssetRequestValidator(TimeProvider time)
    {
        var today = HospitalClock.Today(time);
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.Make).SafeText(100);
        RuleFor(x => x.Model).SafeText(100);
        RuleFor(x => x.SerialNumber).MaximumLength(100);
        RuleFor(x => x.Location).SafeText(200);
        RuleFor(x => x.CustodianName).SafeText(120);
        RuleFor(x => x.PurchaseDate).Must(d => d is null || d <= today).WithMessage("The purchase date cannot be in the future.");
        RuleFor(x => x.PurchaseCost).GreaterThanOrEqualTo(0);
        RuleFor(x => x.SalvageValue).GreaterThanOrEqualTo(0).LessThanOrEqualTo(x => x.PurchaseCost)
            .WithMessage("Salvage value cannot exceed the purchase cost.");
        RuleFor(x => x.UsefulLifeYears).InclusiveBetween(1, 50);
        RuleFor(x => x.DepreciationRatePercent).InclusiveBetween(0, 100);
        RuleFor(x => x.InstallationDate).Must((x, d) => d is null || x.PurchaseDate is null || d >= x.PurchaseDate)
            .WithMessage("Installation cannot be before purchase.");
        RuleFor(x => x.PurchaseOrderNumber).MaximumLength(32);
        RuleFor(x => x.InvoiceNumber).MaximumLength(40);
        RuleFor(x => x.Barcode).MaximumLength(64);
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class SaveAssetContractRequestValidator : AbstractValidator<SaveAssetContractRequest>
{
    public SaveAssetContractRequestValidator()
    {
        RuleFor(x => x.EndDate).GreaterThan(x => x.StartDate).WithMessage("The contract must end after it starts.");
        RuleFor(x => x.Cost).GreaterThanOrEqualTo(0);
        RuleFor(x => x.ContractNumber).MaximumLength(64);
        RuleFor(x => x.CoverageNotes).SafeText(1000);
    }
}

public sealed class RequestCondemnationRequestValidator : AbstractValidator<RequestCondemnationRequest>
{
    public RequestCondemnationRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).SafeText(1000).WithMessage("Explain why the asset should be condemned (at least 10 characters).");
}

public sealed class DecideCondemnationRequestValidator : AbstractValidator<DecideCondemnationRequest>
{
    public DecideCondemnationRequestValidator() => RuleFor(x => x.Remarks).SafeText(500);
}

public sealed class DisposeAssetRequestValidator : AbstractValidator<DisposeAssetRequest>
{
    public DisposeAssetRequestValidator()
    {
        RuleFor(x => x.DisposalValue).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Method).NotEmpty().SafeText(100).WithMessage("How was it disposed of (auction, buy-back, e-waste recycler…)?");
    }
}
