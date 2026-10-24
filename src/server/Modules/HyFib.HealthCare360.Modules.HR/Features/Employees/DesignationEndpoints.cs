using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Features.Employees;

/// <summary>/api/v1/hr/designations — job titles shared by all facilities.</summary>
internal static class DesignationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr/designations").WithTags("HR");

        group.MapGet("/", List).RequireAnyPermission(HRPermissions.EmployeesRead, HRPermissions.EmployeesManage);
        group.MapPost("/", (SaveDesignationRequest r, HospitalDbContext db, CancellationToken ct) => Save(null, r, db, ct))
            .RequirePermission(HRPermissions.EmployeesManage).WithValidation<SaveDesignationRequest>();
        group.MapPut("/{id:guid}", (Guid id, SaveDesignationRequest r, HospitalDbContext db, CancellationToken ct) => Save(id, r, db, ct))
            .RequirePermission(HRPermissions.EmployeesManage).WithValidation<SaveDesignationRequest>().RequireVersion();
    }

    private static async Task<IResult> List(bool? includeInactive, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<Designation>().AsNoTracking()
            .Where(d => includeInactive == true || d.IsActive)
            .OrderBy(d => d.Grade).ThenBy(d => d.Name)
            .Select(d => new DesignationDto(d.Id, d.Code, d.Name, d.Category, d.Grade, d.IsActive, d.Version))
            .ToListAsync(ct));

    private static async Task<IResult> Save(Guid? id, SaveDesignationRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Designation>().AnyAsync(d => d.Code == code && d.Id != id, ct))
        {
            return Error.Conflict("designation.code_in_use", $"Designation code {code} is already used.").ToProblem();
        }

        Designation designation;
        if (id is { } existingId)
        {
            var found = await db.Set<Designation>().FirstOrDefaultAsync(d => d.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(Designation), existingId).ToProblem();
            }

            designation = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(designation, version);
            }
        }
        else
        {
            designation = new Designation();
            db.Add(designation);
        }

        designation.Code = code;
        designation.Name = request.Name.Trim();
        designation.Category = request.Category;
        designation.Grade = request.Grade;
        designation.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/hr/designations/{designation.Id}", new { designation.Id }) : TypedResults.NoContent();
    }
}
