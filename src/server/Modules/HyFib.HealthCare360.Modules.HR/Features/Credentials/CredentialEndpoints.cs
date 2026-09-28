using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Platform.Files;

namespace HyFib.HealthCare360.Modules.HR.Features.Credentials;

/// <summary>/api/v1/hr/credentials — licences and certifications with expiry tracking (HR-08).</summary>
internal static class CredentialEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr").WithTags("HR");

        group.MapGet("/credentials", List).RequireAnyPermission(HRPermissions.CredentialsManage, HRPermissions.EmployeesRead);
        group.MapGet("/employees/{employeeId:guid}/credentials", ForEmployee).RequireAnyPermission(HRPermissions.CredentialsManage, HRPermissions.EmployeesRead);
        group.MapPost("/employees/{employeeId:guid}/credentials", Create).RequirePermission(HRPermissions.CredentialsManage).WithValidation<SaveCredentialRequest>();
        group.MapPut("/credentials/{id:guid}", Update).RequirePermission(HRPermissions.CredentialsManage).WithValidation<SaveCredentialRequest>();
        group.MapDelete("/credentials/{id:guid}", Delete).RequirePermission(HRPermissions.CredentialsManage);
    }

    /// <summary>Expiring/expired credentials first: the list HR works from before NABH audits.</summary>
    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        int? expiringWithinDays,
        CredentialType? type,
        Guid? departmentId,
        HospitalDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.LocalToday();
        var rows = from c in db.Set<Credential>().AsNoTracking()
                   join e in db.Set<Employee>() on c.EmployeeId equals e.Id
                   where e.Status != EmployeeStatus.Separated
                   select new { c, e.EmployeeCode, e.FullName, e.DepartmentId };

        if (expiringWithinDays is { } days)
        {
            var limit = today.AddDays(Math.Clamp(days, 0, 3650));
            rows = rows.Where(r => r.c.ExpiresOn != null && r.c.ExpiresOn <= limit);
        }

        if (type is not null)
        {
            rows = rows.Where(r => r.c.Type == type);
        }

        if (departmentId is not null)
        {
            rows = rows.Where(r => r.DepartmentId == departmentId);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            rows = rows.Where(r => EF.Functions.ILike(r.FullName, like) || r.EmployeeCode == term.ToUpper() || EF.Functions.ILike(r.c.Number, like));
        }

        var result = await rows.OrderBy(r => r.c.ExpiresOn == null).ThenBy(r => r.c.ExpiresOn).ThenBy(r => r.FullName).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(r => ToDto(r.c, r.EmployeeCode, r.FullName, today)));
    }

    private static async Task<IResult> ForEmployee(Guid employeeId, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == employeeId).Select(e => new { e.EmployeeCode, e.FullName }).FirstOrDefaultAsync(ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId).ToProblem();
        }

        var today = time.LocalToday();
        // Sorted in memory: the type is stored as text, and SQL would order it alphabetically rather than by enum order.
        var credentials = await db.Set<Credential>().AsNoTracking().Where(c => c.EmployeeId == employeeId).ToListAsync(ct);
        return TypedResults.Ok(credentials.OrderBy(c => c.Type).ThenBy(c => c.ExpiresOn)
            .Select(c => ToDto(c, employee.EmployeeCode, employee.FullName, today)).ToList());
    }

    private static async Task<IResult> Create(Guid employeeId, SaveCredentialRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().Where(e => e.Id == employeeId).Select(e => new { e.FacilityId, e.Status }).FirstOrDefaultAsync(ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId).ToProblem();
        }

        if (employee.Status == EmployeeStatus.Separated)
        {
            return Error.Rule("employee.separated", "Credentials cannot be added to a separated employee.").ToProblem();
        }

        if (request.FileId is { } fileId && !await db.Set<StoredFile>().AnyAsync(f => f.Id == fileId, ct))
        {
            return Error.Field("fileId", "Upload the certificate first.").ToProblem();
        }

        var credential = new Credential { FacilityId = employee.FacilityId, EmployeeId = employeeId };
        Apply(credential, request);
        db.Add(credential);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/hr/credentials/{credential.Id}", new { credential.Id });
    }

    private static async Task<IResult> Update(Guid id, SaveCredentialRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var credential = await db.Set<Credential>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null)
        {
            return Error.NotFound(nameof(Credential), id).ToProblem();
        }

        if (request.FileId is { } fileId && fileId != credential.FileId && !await db.Set<StoredFile>().AnyAsync(f => f.Id == fileId, ct))
        {
            return Error.Field("fileId", "Upload the certificate first.").ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(credential, version);
        }

        Apply(credential, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Delete(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var credential = await db.Set<Credential>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (credential is null)
        {
            return Error.NotFound(nameof(Credential), id).ToProblem();
        }

        db.Remove(credential);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static void Apply(Credential credential, SaveCredentialRequest request)
    {
        credential.Type = request.Type;
        credential.Name = request.Name.Trim();
        credential.Number = request.Number.Trim();
        credential.IssuingAuthority = string.IsNullOrWhiteSpace(request.IssuingAuthority) ? null : request.IssuingAuthority.Trim();
        credential.SetValidity(request.IssuedOn, request.ExpiresOn);
        credential.FileId = request.FileId;
        credential.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
    }

    internal static CredentialDto ToDto(Credential c, string employeeCode, string employeeName, DateOnly today) => new(
        c.Id, c.EmployeeId, employeeCode, employeeName, c.Type, c.Name, c.Number, c.IssuingAuthority, c.IssuedOn, c.ExpiresOn,
        CredentialExpiryPolicy.StageFor(c.ExpiresOn, today), c.ExpiresOn is { } expiry ? expiry.DayNumber - today.DayNumber : null,
        c.FileId, c.Notes, c.Version);
}
