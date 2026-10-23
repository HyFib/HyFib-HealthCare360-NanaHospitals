using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Assets;

/// <summary>/api/v1/assets — asset register, AMC/CMC contracts, depreciation, condemnation and disposal (AST-01/02/06).</summary>
internal static class AssetEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/assets").WithTags("Assets");

        group.MapGet("/", List).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapGet("/dashboard", Dashboard).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapGet("/by-tag/{tag}", ByTag).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapPost("/", async (SaveAssetRequest r, AssetService assets, CancellationToken ct) =>
                (await assets.CreateAsync(r, ct)).ToCreated(d => $"/api/v1/assets/{d.Id}"))
            .RequirePermission(SupplyChainPermissions.AssetsManage).WithValidation<SaveAssetRequest>();
        group.MapPut("/{id:guid}", async (Guid id, SaveAssetRequest r, AssetService assets, CancellationToken ct) =>
                (await assets.UpdateAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AssetsManage).WithValidation<SaveAssetRequest>().RequireVersion();
        group.MapPost("/{id:guid}/operational", async (Guid id, SetAssetOperationalRequest r, AssetService assets, CancellationToken ct) =>
                (await assets.SetOperationalAsync(id, r.InUse, ct)).ToNoContent())
            .RequireAnyPermission(SupplyChainPermissions.AssetsManage, SupplyChainPermissions.AssetsMaintenance);

        group.MapGet("/{id:guid}/contracts", Contracts).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapPost("/{id:guid}/contracts", async (Guid id, SaveAssetContractRequest r, AssetService assets, CancellationToken ct) =>
                (await assets.SaveContractAsync(id, null, r, ct)).ToCreated(contractId => $"/api/v1/assets/{id}/contracts/{contractId}"))
            .RequirePermission(SupplyChainPermissions.AssetsManage).WithValidation<SaveAssetContractRequest>();
        group.MapPut("/{id:guid}/contracts/{contractId:guid}", async (Guid id, Guid contractId, SaveAssetContractRequest r, AssetService assets, CancellationToken ct) =>
                (await assets.SaveContractAsync(id, contractId, r, ct)).ToHttpResult(_ => TypedResults.NoContent()))
            .RequirePermission(SupplyChainPermissions.AssetsManage).WithValidation<SaveAssetContractRequest>().RequireVersion();

        group.MapGet("/{id:guid}/depreciation", async (Guid id, DateOnly? asOf, AssetService assets, CancellationToken ct) =>
                (await assets.DepreciationAsync(id, asOf, ct)).ToOk())
            .RequireAnyPermission(SupplyChainPermissions.AssetReaders);

        group.MapPost("/{id:guid}/condemnation", async (Guid id, RequestCondemnationRequest r, AssetService assets, CancellationToken ct) =>
                (await assets.RequestCondemnationAsync(id, r.Reason, ct)).ToCreated(requestId => $"/api/v1/assets/condemnations/{requestId}"))
            .RequireAnyPermission(SupplyChainPermissions.AssetsMaintenance, SupplyChainPermissions.AssetsManage).WithValidation<RequestCondemnationRequest>();
        group.MapGet("/condemnations", Condemnations).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapPost("/condemnations/{id:guid}/approve", async (Guid id, DecideCondemnationRequest r, AssetService assets, CancellationToken ct) =>
                (await assets.DecideCondemnationAsync(id, approve: true, r.Remarks, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AssetsManage).WithValidation<DecideCondemnationRequest>();
        group.MapPost("/condemnations/{id:guid}/reject", async (Guid id, DecideCondemnationRequest r, AssetService assets, CancellationToken ct) =>
                (await assets.DecideCondemnationAsync(id, approve: false, r.Remarks, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AssetsManage).WithValidation<DecideCondemnationRequest>();
        group.MapPost("/{id:guid}/dispose", async (Guid id, DisposeAssetRequest r, AssetService assets, CancellationToken ct) =>
                (await assets.DisposeAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AssetsManage).WithValidation<DisposeAssetRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        AssetCategory? category,
        AssetStatus? status,
        AssetCriticality? criticality,
        Guid? departmentId,
        bool? includeRetired,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var assets = db.Set<Asset>().AsNoTracking();
        if (includeRetired != true && status is null)
        {
            assets = assets.Where(a => a.Status != AssetStatus.Condemned && a.Status != AssetStatus.Disposed);
        }

        if (category is not null)
        {
            assets = assets.Where(a => a.Category == category);
        }

        if (status is not null)
        {
            assets = assets.Where(a => a.Status == status);
        }

        if (criticality is not null)
        {
            assets = assets.Where(a => a.Criticality == criticality);
        }

        if (departmentId is not null)
        {
            assets = assets.Where(a => a.DepartmentId == departmentId);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            var upper = term.ToUpperInvariant();
            assets = assets.Where(a => a.AssetTag == upper || a.Barcode == term || a.SerialNumber == term || EF.Functions.ILike(a.Name, like, QueryExtensions.LikeEscape)
                                       || (a.Model != null && EF.Functions.ILike(a.Model, like, QueryExtensions.LikeEscape)));
        }

        var result = await assets.OrderBy(a => a.Name).ThenBy(a => a.AssetTag)
            .Select(a => new AssetListItem(
                a.Id, a.AssetTag, a.Name, a.Category, a.Make, a.Model, a.SerialNumber, a.DepartmentId,
                db.Set<Department>().Where(d => d.Id == a.DepartmentId).Select(d => d.Name).FirstOrDefault(),
                a.Location, a.Status, a.Criticality, a.WarrantyExpiry,
                db.Set<PreventiveMaintenancePlan>().Where(p => p.AssetId == a.Id && p.IsActive).Min(p => (DateOnly?)p.NextDueDate),
                a.NextCalibrationDue,
                db.Set<MaintenanceTicket>().Count(t => t.AssetId == a.Id
                                                       && (t.Status == TicketStatus.Open || t.Status == TicketStatus.Assigned || t.Status == TicketStatus.InProgress))))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var asset = await db.Set<Asset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return asset is null ? Error.NotFound(nameof(Asset), id).ToProblem() : TypedResults.Ok(await ToDetailAsync(asset, db, time, ct));
    }

    /// <summary>Scanning the asset label (tag or barcode) opens the asset.</summary>
    private static async Task<IResult> ByTag(string tag, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        // An asset tag format may contain "/". The staff app sends it as %2F, which ASP.NET Core leaves encoded in a route value.
        var normalized = tag.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase).Trim();
        var upper = normalized.ToUpperInvariant();
        var asset = await db.Set<Asset>().AsNoTracking().FirstOrDefaultAsync(a => a.AssetTag == upper || a.Barcode == normalized, ct);
        return asset is null ? Error.NotFound(nameof(Asset), normalized).ToProblem() : TypedResults.Ok(await ToDetailAsync(asset, db, time, ct));
    }

    private static async Task<IResult> Contracts(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = HospitalClock.Today(time);
        var rows = await db.Set<AssetContract>().AsNoTracking().Where(c => c.AssetId == id)
            .OrderByDescending(c => c.EndDate)
            .Select(c => new
            {
                c.Id,
                c.AssetId,
                c.Type,
                c.VendorId,
                VendorName = db.Set<Vendor>().Where(v => v.Id == c.VendorId).Select(v => v.Name).FirstOrDefault(),
                c.ContractNumber,
                c.StartDate,
                c.EndDate,
                c.Cost,
                c.CoverageNotes,
                c.Version,
            })
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(c => new AssetContractDto(
            c.Id, c.AssetId, c.Type, c.VendorId, c.VendorName, c.ContractNumber, c.StartDate, c.EndDate, c.Cost, c.CoverageNotes,
            c.StartDate <= today && today <= c.EndDate, c.Version)).ToList());
    }

    private static async Task<IResult> Condemnations(CondemnationStatus? status, HospitalDbContext db, CancellationToken ct)
    {
        var requests = db.Set<CondemnationRequest>().AsNoTracking();
        if (status is not null)
        {
            requests = requests.Where(r => r.Status == status);
        }

        var rows = await (
                from r in requests
                join a in db.Set<Asset>() on r.AssetId equals a.Id
                orderby r.RequestedAt descending
                select new CondemnationDto(
                    r.Id, r.AssetId, a.AssetTag, a.Name, r.Reason, r.BookValueAtRequest, r.Status, r.RequestedAt,
                    db.Set<User>().Where(u => u.Id == r.RequestedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                    r.DecidedAt, r.DecisionRemarks))
            .Take(200)
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> Dashboard(HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = HospitalClock.Today(time);
        var monthStart = HospitalClock.StartOfDayUtc(new DateOnly(today.Year, today.Month, 1));
        var assets = await db.Set<Asset>().AsNoTracking().ToListAsync(ct);
        var inService = assets.Where(a => a.IsInService).ToList();
        var inServiceIds = inService.Select(a => a.Id).ToList();

        var openTickets = await db.Set<MaintenanceTicket>().AsNoTracking()
            .Where(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.Assigned || t.Status == TicketStatus.InProgress)
            .Select(t => t.Priority)
            .ToListAsync(ct);
        var downtimeMinutes = await db.Set<MaintenanceTicket>().AsNoTracking()
            .Where(t => t.ResolvedAt >= monthStart && t.DowntimeMinutes != null)
            .SumAsync(t => t.DowntimeMinutes ?? 0, ct);
        var plans = await db.Set<PreventiveMaintenancePlan>().AsNoTracking()
            .Where(p => p.IsActive && inServiceIds.Contains(p.AssetId))
            .Select(p => p.NextDueDate)
            .ToListAsync(ct);
        var due = await MaintenanceService.DueItemsAsync(db, today, MaintenanceService.DueWindowDays, MaintenanceService.ExpiryWindowDays, ct);

        return TypedResults.Ok(new AssetDashboard(
            inService.Count,
            assets.GroupBy(a => a.Status).Select(g => new AssetStatusCount(g.Key, g.Count())).OrderBy(s => s.Status).ToList(),
            openTickets.Count,
            openTickets.Count(p => p == TicketPriority.Critical),
            plans.Count(d => d >= today && d <= today.AddDays(MaintenanceService.DueWindowDays)),
            plans.Count(d => d < today),
            due.Count(d => d.Kind == MaintenanceService.CalibrationKind),
            due.Count(d => d.Kind == MaintenanceService.WarrantyKind),
            due.Count(d => d.Kind == MaintenanceService.ContractKind),
            Math.Round(downtimeMinutes / 60m, 1),
            inService.Sum(a => AssetService.Depreciate(a, today).BookValue)));
    }

    private static async Task<AssetDetail> ToDetailAsync(Asset a, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = HospitalClock.Today(time);
        var department = a.DepartmentId is { } departmentId
            ? await db.Set<Department>().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;
        var vendor = a.VendorId is { } vendorId
            ? await db.Set<Vendor>().Where(v => v.Id == vendorId).Select(v => v.Name).FirstOrDefaultAsync(ct)
            : null;
        var (_, rate, bookValue) = AssetService.Depreciate(a, today);
        return new AssetDetail(
            a.Id, a.AssetTag, a.Barcode, a.Name, a.Category, a.Make, a.Model, a.SerialNumber, a.DepartmentId, department, a.Location,
            a.CustodianUserId, a.CustodianName, a.PurchaseDate, a.PurchaseCost, a.VendorId, vendor, a.PurchaseOrderNumber, a.InvoiceNumber,
            a.InstallationDate, a.WarrantyExpiry, a.WarrantyExpiry >= today, a.Status, a.Criticality, a.DepreciationMethod, a.UsefulLifeYears,
            a.SalvageValue, rate, bookValue, a.RequiresCalibration, a.NextCalibrationDue, a.CondemnedOn, a.DisposedOn, a.DisposalValue,
            a.DisposalMethod, a.Notes, a.Version);
    }
}
