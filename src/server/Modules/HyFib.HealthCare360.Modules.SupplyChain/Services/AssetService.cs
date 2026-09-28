using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Assets;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Asset register (AST-01/02/06): tagging, AMC/CMC contracts, depreciation and the condemnation → disposal
/// life cycle. Condemnation is decided by a second person and records the book value at the time of request.
/// </summary>
public sealed class AssetService(HospitalDbContext db, INumberSeries numbers, ICurrentUser currentUser, TimeProvider time)
{
    public const string TagSeries = "supplychain.asset";
    public const string TagFormat = "{FAC}AS{SEQ:6}";

    public async Task<Result<DocumentCreated>> CreateAsync(SaveAssetRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var references = await CheckReferencesAsync(request, null, ct);
        if (references.IsFailure)
        {
            return references.Error!;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var asset = new Asset
        {
            FacilityId = facilityId,
            AssetTag = await numbers.NextAsync(TagSeries, facilityId, TagFormat, NumberResetPolicy.Never, ct),
        };
        Apply(asset, request);
        asset.Barcode ??= asset.AssetTag;
        db.Add(asset);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new DocumentCreated(asset.Id, asset.AssetTag);
    }

    public async Task<Result> UpdateAsync(Guid id, SaveAssetRequest request, CancellationToken ct)
    {
        var asset = await db.Set<Asset>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null)
        {
            return Error.NotFound(nameof(Asset), id);
        }

        if (!asset.IsInService)
        {
            return Error.Rule("asset.retired", $"Asset {asset.AssetTag} is {asset.Status} and can no longer be edited.");
        }

        var references = await CheckReferencesAsync(request, id, ct);
        if (references.IsFailure)
        {
            return references;
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(asset, version);
        }

        Apply(asset, request);
        asset.Barcode ??= asset.AssetTag;
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public Task<Result> SetOperationalAsync(Guid id, bool inUse, CancellationToken ct) => ChangeAsync(id, a => a.SetOperational(inUse), ct);

    // ------------------------------------------------------------------ contracts

    public async Task<Result<Guid>> SaveContractAsync(Guid assetId, Guid? contractId, SaveAssetContractRequest request, CancellationToken ct)
    {
        if (!await db.Set<Asset>().AnyAsync(a => a.Id == assetId, ct))
        {
            return Error.NotFound(nameof(Asset), assetId);
        }

        if (request.VendorId is { } vendorId && !await db.Set<Vendor>().AnyAsync(v => v.Id == vendorId, ct))
        {
            return Error.Field("vendorId", "Choose a valid vendor.");
        }

        AssetContract contract;
        if (contractId is { } existingId)
        {
            var found = await db.Set<AssetContract>().FirstOrDefaultAsync(c => c.Id == existingId && c.AssetId == assetId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(AssetContract), existingId);
            }

            contract = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(contract, version);
            }
        }
        else
        {
            contract = new AssetContract { AssetId = assetId };
            db.Add(contract);
        }

        contract.Type = request.Type;
        contract.VendorId = request.VendorId;
        contract.ContractNumber = request.ContractNumber?.Trim();
        contract.StartDate = request.StartDate;
        contract.EndDate = request.EndDate;
        contract.Cost = request.Cost;
        contract.CoverageNotes = request.CoverageNotes?.Trim();
        await db.SaveChangesAsync(ct);
        return contract.Id;
    }

    // ------------------------------------------------------------------ depreciation

    public async Task<Result<AssetDepreciation>> DepreciationAsync(Guid id, DateOnly? asOf, CancellationToken ct)
    {
        var asset = await db.Set<Asset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null)
        {
            return Error.NotFound(nameof(Asset), id);
        }

        var date = asOf ?? HospitalClock.Today(time);
        var (schedule, rate, bookValue) = Depreciate(asset, date);
        return new AssetDepreciation(asset.Id, asset.DepreciationMethod, asset.PurchaseCost, asset.SalvageValue, rate, asset.DepreciationStart, date,
            bookValue, asset.PurchaseCost - bookValue, schedule);
    }

    /// <summary>
    /// Schedule, effective rate and book value. WDV without a configured rate uses the rate that reaches salvage at
    /// the end of the useful life; disposed assets have no book value.
    /// </summary>
    internal static (IReadOnlyList<DepreciationYear> Schedule, decimal Rate, decimal BookValue) Depreciate(Asset asset, DateOnly asOf)
    {
        if (asset.Status == AssetStatus.Disposed)
        {
            return ([], asset.DepreciationRatePercent, 0m);
        }

        if (asset.DepreciationStart is not { } start || asset.PurchaseCost <= 0)
        {
            return ([], asset.DepreciationRatePercent, asset.PurchaseCost);
        }

        var cost = asset.PurchaseCost;
        var salvage = Math.Min(asset.SalvageValue, cost);
        var life = Math.Max(asset.UsefulLifeYears, 1);
        if (asset.DepreciationMethod == DepreciationMethod.WrittenDownValue)
        {
            var rate = asset.DepreciationRatePercent > 0 ? asset.DepreciationRatePercent : DepreciationCalculator.WdvRateFor(cost, salvage, life);
            if (rate <= 0)
            {
                return ([], 0m, cost);
            }

            var wdv = DepreciationCalculator.WrittenDownValue(cost, salvage, rate, life, start);
            return (wdv, rate, DepreciationCalculator.BookValue(wdv, cost, asOf));
        }

        var straight = DepreciationCalculator.StraightLine(cost, salvage, life, start);
        return (straight, cost == 0 ? 0 : Math.Round((cost - salvage) * 100m / cost / life, 2), DepreciationCalculator.BookValue(straight, cost, asOf));
    }

    // ------------------------------------------------------------------ condemnation and disposal

    public async Task<Result<Guid>> RequestCondemnationAsync(Guid assetId, string reason, CancellationToken ct)
    {
        var asset = await db.Set<Asset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null)
        {
            return Error.NotFound(nameof(Asset), assetId);
        }

        if (!asset.IsInService)
        {
            return Error.Rule("asset.retired", $"Asset {asset.AssetTag} is already {asset.Status}.");
        }

        if (await db.Set<CondemnationRequest>().AnyAsync(c => c.AssetId == assetId && c.Status == CondemnationStatus.Pending, ct))
        {
            return Error.Conflict("condemnation.pending", "A condemnation request for this asset is already pending.");
        }

        var request = new CondemnationRequest
        {
            FacilityId = asset.FacilityId,
            AssetId = assetId,
            Reason = reason.Trim(),
            RequestedByUserId = currentUser.UserId,
            RequestedAt = time.GetUtcNow(),
            BookValueAtRequest = Depreciate(asset, HospitalClock.Today(time)).BookValue,
        };
        db.Add(request);
        await db.SaveChangesAsync(ct);
        return request.Id;
    }

    public async Task<Result> DecideCondemnationAsync(Guid requestId, bool approve, string? remarks, CancellationToken ct)
    {
        var request = await db.Set<CondemnationRequest>().FirstOrDefaultAsync(c => c.Id == requestId, ct);
        if (request is null)
        {
            return Error.NotFound(nameof(CondemnationRequest), requestId);
        }

        var now = time.GetUtcNow();
        var decided = approve
            ? request.Approve(currentUser.UserId, remarks?.Trim(), now)
            : request.Reject(currentUser.UserId, remarks?.Trim() ?? "Rejected", now);
        if (decided.IsFailure)
        {
            return decided;
        }

        if (approve)
        {
            var asset = await db.Set<Asset>().FirstAsync(a => a.Id == request.AssetId, ct);
            var condemned = asset.Condemn(HospitalClock.ToLocalDate(now));
            if (condemned.IsFailure)
            {
                return condemned;
            }
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public Task<Result> DisposeAsync(Guid assetId, DisposeAssetRequest request, CancellationToken ct) =>
        ChangeAsync(assetId, a => a.Dispose(request.DisposedOn, request.DisposalValue, request.Method.Trim()), ct);

    // ------------------------------------------------------------------ helpers

    private async Task<Result> CheckReferencesAsync(SaveAssetRequest request, Guid? assetId, CancellationToken ct)
    {
        if (request.DepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId, ct))
        {
            return Error.Field("departmentId", "Choose a department of this facility.");
        }

        if (request.VendorId is { } vendorId && !await db.Set<Vendor>().AnyAsync(v => v.Id == vendorId, ct))
        {
            return Error.Field("vendorId", "Choose a valid vendor.");
        }

        var serial = request.SerialNumber?.Trim();
        if (!string.IsNullOrEmpty(serial)
            && await db.Set<Asset>().AnyAsync(a => a.SerialNumber == serial && a.Make == request.Make && a.Id != assetId, ct))
        {
            return Error.Conflict("asset.duplicate_serial", $"An asset with serial number {serial} from this make is already registered.");
        }

        return Result.Success();
    }

    private static void Apply(Asset asset, SaveAssetRequest request)
    {
        asset.Name = request.Name.Trim();
        asset.Category = request.Category;
        asset.Make = request.Make?.Trim();
        asset.Model = request.Model?.Trim();
        asset.SerialNumber = request.SerialNumber?.Trim();
        asset.DepartmentId = request.DepartmentId;
        asset.Location = request.Location?.Trim();
        asset.CustodianUserId = request.CustodianUserId;
        asset.CustodianName = request.CustodianName?.Trim();
        asset.PurchaseDate = request.PurchaseDate;
        asset.PurchaseCost = request.PurchaseCost;
        asset.VendorId = request.VendorId;
        asset.PurchaseOrderNumber = request.PurchaseOrderNumber?.Trim();
        asset.InvoiceNumber = request.InvoiceNumber?.Trim();
        asset.InstallationDate = request.InstallationDate;
        asset.WarrantyExpiry = request.WarrantyExpiry;
        asset.Criticality = request.Criticality;
        asset.DepreciationMethod = request.DepreciationMethod;
        asset.UsefulLifeYears = request.UsefulLifeYears;
        asset.SalvageValue = request.SalvageValue;
        asset.DepreciationRatePercent = request.DepreciationRatePercent ?? 0;
        asset.RequiresCalibration = request.RequiresCalibration;
        asset.Barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim();
        asset.Notes = request.Notes?.Trim();
    }

    private async Task<Result> ChangeAsync(Guid id, Func<Asset, Result> change, CancellationToken ct)
    {
        var asset = await db.Set<Asset>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null)
        {
            return Error.NotFound(nameof(Asset), id);
        }

        var result = change(asset);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }
}
