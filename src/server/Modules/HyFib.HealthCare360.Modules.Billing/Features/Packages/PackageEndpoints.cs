using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Packages;

/// <summary>
/// /api/v1/billing/packages — fixed-price treatment packages with inclusions and caps (MB-02, IPB-04), for admissions,
/// out-patient visits or both. Whoever prepares OP bills reads them to choose a bill's package.
/// </summary>
internal static class PackageEndpoints
{
    private static readonly string[] Readers =
        [BillingPermissions.TariffsManage, BillingPermissions.IpManage, BillingPermissions.ChargesRead, BillingPermissions.OpCreate];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/packages").WithTags("Billing - Packages");
        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapPost("/", Create).RequirePermission(BillingPermissions.TariffsManage).WithValidation<SavePackageRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(BillingPermissions.TariffsManage).WithValidation<SavePackageRequest>();
    }

    private static async Task<IResult> List(bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var packages = db.Set<Package>().AsNoTracking();
        if (includeInactive != true)
        {
            packages = packages.Where(p => p.IsActive);
        }

        var rows = await packages.OrderBy(p => p.Name)
            .Select(p => new PackageListItem(p.Id, p.Code, p.Name, p.Price, p.LengthOfStayDays, p.TariffPlanId, p.UsedFor, p.IsActive, p.Inclusions.Count))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var package = await db.Set<Package>().AsNoTracking().Include(p => p.Inclusions).FirstOrDefaultAsync(p => p.Id == id, ct);
        return package is null ? Error.NotFound(nameof(Package), id).ToProblem() : TypedResults.Ok(await ToDetailAsync(db, package, ct));
    }

    private static async Task<IResult> Create(SavePackageRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Package>().IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct))
        {
            return Error.Conflict("billing.package_code_exists", $"A package with code {code} already exists.").ToProblem();
        }

        var package = new Package { Code = code };
        var applied = await ApplyAsync(db, package, request, ct);
        if (applied.IsFailure)
        {
            return applied.Error!.ToProblem();
        }

        db.Add(package);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/billing/packages/{package.Id}", await ToDetailAsync(db, package, ct));
    }

    private static async Task<IResult> Update(Guid id, SavePackageRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var package = await db.Set<Package>().Include(p => p.Inclusions).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (package is null)
        {
            return Error.NotFound(nameof(Package), id).ToProblem();
        }

        db.GuardVersion(package, request.Version ?? 0);
        var applied = await ApplyAsync(db, package, request, ct);
        if (applied.IsFailure)
        {
            return applied.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ToDetailAsync(db, package, ct));
    }

    private static async Task<Result> ApplyAsync(HospitalDbContext db, Package package, SavePackageRequest request, CancellationToken ct)
    {
        var item = await db.Set<ServiceItem>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServiceItemId, ct);
        if (item is null || item.Category != ServiceCategory.Package)
        {
            return Error.Field("serviceItemId", "Choose a catalogue service of category Package to bill the package under.");
        }

        var includedIds = request.Inclusions.Where(i => i.ServiceItemId is not null).Select(i => i.ServiceItemId!.Value).Distinct().ToList();
        if (includedIds.Count > 0 && await db.Set<ServiceItem>().CountAsync(s => includedIds.Contains(s.Id), ct) != includedIds.Count)
        {
            return Error.Field("inclusions", "One or more included services were not found.");
        }

        package.Name = request.Name.Trim();
        package.ServiceItemId = request.ServiceItemId;
        package.Price = request.Price;
        package.LengthOfStayDays = request.LengthOfStayDays;
        package.TariffPlanId = request.TariffPlanId;
        package.UsedFor = request.UsedFor;
        package.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        package.IsActive = request.IsActive;
        package.Inclusions.Clear();
        foreach (var inclusion in request.Inclusions)
        {
            package.Inclusions.Add(new PackageInclusion
            {
                PackageId = package.Id,
                ServiceItemId = inclusion.ServiceItemId,
                Category = inclusion.ServiceItemId is null ? inclusion.Category : null,
                MaxQuantity = inclusion.MaxQuantity,
                MaxAmount = inclusion.MaxAmount,
                Notes = string.IsNullOrWhiteSpace(inclusion.Notes) ? null : inclusion.Notes.Trim(),
            });
        }

        return Result.Success();
    }

    private static async Task<PackageDetail> ToDetailAsync(HospitalDbContext db, Package package, CancellationToken ct)
    {
        var ids = package.Inclusions.Where(i => i.ServiceItemId is not null).Select(i => i.ServiceItemId!.Value).Append(package.ServiceItemId).Distinct().ToList();
        var items = await db.Set<ServiceItem>().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => new { s.Code, s.Name }, ct);
        return new PackageDetail(
            package.Id, package.Code, package.Name, package.ServiceItemId, items.GetValueOrDefault(package.ServiceItemId)?.Code, package.Price,
            package.LengthOfStayDays, package.TariffPlanId, package.UsedFor, package.Description, package.IsActive,
            package.Inclusions.Select(i => new PackageInclusionDto(
                i.Id, i.ServiceItemId, i.ServiceItemId is { } sid ? items.GetValueOrDefault(sid)?.Code : null,
                i.ServiceItemId is { } nid ? items.GetValueOrDefault(nid)?.Name : null, i.Category, i.MaxQuantity, i.MaxAmount, i.Notes)).ToList(),
            package.Version);
    }
}
