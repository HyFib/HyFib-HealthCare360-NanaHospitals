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
            .RequirePermission(ClinicalPermissions.HealthCheckManage).WithValidation<SavePackageRequest>();
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
            packages = packages.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Code, like));
        }

        return TypedResults.Ok((await packages.OrderBy(p => p.Price).ToListAsync(ct)).Select(HealthCheckupService.ToDto).ToList());
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var package = await db.Set<HealthPackage>().AsNoTracking().Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct);
        return package is null ? Error.NotFound("Health package", id).ToProblem() : TypedResults.Ok(HealthCheckupService.ToDto(package));
    }

    private static async Task<IResult> Eligibility(Guid id, Guid patientId, HealthCheckupService checkups, CancellationToken ct) =>
        (await checkups.EligibilityAsync(id, patientId, ct)).ToOk();

    private static async Task<IResult> Save(Guid? id, SavePackageRequest request, HealthCheckupService checkups, CancellationToken ct)
    {
        var saved = await checkups.SavePackageAsync(id, request, ct);
        return id is null ? saved.ToCreated(p => $"/api/v1/health-checkups/packages/{p.Id}") : saved.ToOk();
    }
}
