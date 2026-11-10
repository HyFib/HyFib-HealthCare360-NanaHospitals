using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;

/// <summary>/api/v1/health-checkups/packages — the health package master (MHC-01).</summary>
internal static class PackageEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/health-checkups/packages").WithTags("Health Checkups");

        group.MapGet("/", List).RequireAnyPermission(ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}/eligibility", Eligibility).RequireAnyPermission(ClinicalPermissions.HealthCheckManage, ClinicalPermissions.EmrRead);
        group.MapPost("/", (SavePackageRequest r, HealthCheckupService s, CancellationToken ct) => Save(null, r, s, ct))
            .RequirePermission(ClinicalPermissions.HealthCheckManage).WithValidation<SavePackageRequest>();
        group.MapPut("/{id:guid}", (Guid id, SavePackageRequest r, HealthCheckupService s, CancellationToken ct) => Save(id, r, s, ct))
            .RequirePermission(ClinicalPermissions.HealthCheckManage).WithValidation<SavePackageRequest>().RequireVersion();
    }

    private static async Task<IResult> List(string? search, bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var packages = db.Set<HealthPackage>().AsNoTracking().Include(p => p.Items).AsQueryable();
        if (includeInactive != true)
        {
            packages = packages.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = search.Trim().ToLikePattern();
            packages = packages.Where(p => EF.Functions.ILike(p.Name, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(p.Code, like, QueryExtensions.LikeEscape));
        }

        return TypedResults.Ok((await packages.OrderBy(p => p.Price).ToListAsync(ct)).Select(HealthCheckupService.ToDto).ToList());
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, ServicePrices prices, CancellationToken ct)
    {
        var package = await db.Set<HealthPackage>().AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct);
        return package is null
            ? Error.NotFound("Health package", id).ToProblem()
            : TypedResults.Ok(HealthCheckupService.ToDto(package) with
            {
                PendingPrice = package.ServiceItemId is { } serviceId ? await prices.PendingAsync(serviceId, ct) : null,
            });
    }

    private static async Task<IResult> Eligibility(Guid id, Guid patientId, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.EligibilityAsync(id, patientId, ct)).ToOk();

    /// <summary>
    /// 201 with the new package (out of use until its price is approved); 202 with the request when a save asks a second
    /// person for a price or for putting the package in use; 200 when it was saved at once; 409 while another change to its
    /// price waits.
    /// </summary>
    private static async Task<IResult> Save(Guid? id, SavePackageRequest request, HealthCheckupService checkups, CancellationToken ct)
    {
        var saved = await checkups.SavePackageAsync(id, request, ct);
        if (saved.IsFailure)
        {
            return saved.Error!.ToProblem();
        }

        var (package, asked) = saved.Value;
        return id is null
            ? TypedResults.Created($"/api/v1/health-checkups/packages/{package.Id}", package)
            : asked is not null
                ? TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked)
                : TypedResults.Ok(package);
    }
}
