using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

/// <summary>/api/v1/patients — search, register, view, update, merge and restricted-record access.</summary>
internal static class PatientEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/patients").WithTags("Patients");

        group.MapGet("/", Search).RequirePermission(PatientsPermissions.Read);
        group.MapGet("/{id:guid}", Get).RequirePermission(PatientsPermissions.Read);
        group.MapGet("/{id:guid}/summary", GetSummary).RequirePermission(PatientsPermissions.Read);
        group.MapGet("/by-uhid/{uhid}", GetByUhid).RequirePermission(PatientsPermissions.Read);
        group.MapPost("/duplicates", CheckDuplicates).RequirePermission(PatientsPermissions.Register);
        group.MapPost("/", Register).RequirePermission(PatientsPermissions.Register).WithValidation<RegisterPatientRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(PatientsPermissions.Register).WithValidation<UpdatePatientRequest>();
        group.MapPost("/{id:guid}/merge", Merge).RequirePermission(PatientsPermissions.Merge).WithValidation<MergePatientRequest>();
        group.MapPost("/{id:guid}/break-glass", BreakGlass).RequirePermission(PatientsPermissions.BreakGlass).WithValidation<BreakGlassRequest>();

        // Patient Portal: the signed-in patient's own record and family members sharing the verified mobile number.
        app.MapGet("/api/v1/portal/profiles", PortalProfiles).WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal);
    }

    private static async Task<IResult> PortalProfiles(PortalPatientScope scope, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var ids = await scope.AccessiblePatientIdsAsync(ct);
        var summaries = await PatientQueries.SummariesAsync(db, ids, time.Today(), ct);
        return TypedResults.Ok(ids.Where(summaries.ContainsKey).Select(id => summaries[id]).ToList());
    }

    private static async Task<IResult> Search(
        [AsParameters] PageQuery query,
        Gender? gender,
        PatientCategory? category,
        bool? includeInactive,
        HospitalDbContext db,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var patients = db.Set<Patient>().AsNoTracking().Search(page.SearchTerm, protector);
        if (includeInactive != true)
        {
            patients = patients.Where(p => p.IsActive && p.MergedIntoPatientId == null);
        }

        if (gender is not null)
        {
            patients = patients.Where(p => p.Gender == gender);
        }

        if (category is not null)
        {
            patients = patients.Where(p => p.Category == category);
        }

        var ordered = page.SearchTerm is null ? patients.OrderByDescending(p => p.RegisteredAt) : patients.OrderBy(p => p.FullName).ThenByDescending(p => p.RegisteredAt);
        var result = await ordered
            .Select(p => new
            {
                p.Id,
                p.Uhid,
                p.FullName,
                p.Gender,
                p.DateOfBirth,
                p.Phone,
                p.Category,
                p.IsVip,
                p.IsDeceased,
                p.RegisteredAt,
                LastVisitAt = db.Set<Visit>().Where(v => v.PatientId == p.Id).Max(v => (DateTimeOffset?)v.VisitedAt),
            })
            .ToPagedResultAsync(page, ct);

        var today = time.Today();
        return TypedResults.Ok(result.Map(p => new PatientListItem(
            p.Id, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, p.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            Phone.Mask(p.Phone), p.Category, p.IsVip, p.IsDeceased, p.RegisteredAt, p.LastVisitAt)));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var p = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return Error.NotFound(nameof(Patient), id).ToProblem();
        }

        await audit.PatientRecordAccessedAsync(id, "Viewed demographics", ct);
        var today = time.Today();
        return TypedResults.Ok(new PatientDetail(
            p.Id, p.Uhid, p.Title, p.FirstName, p.MiddleName, p.LastName, p.FullName, p.Gender, p.DateOfBirth, p.IsDateOfBirthEstimated,
            p.AgeDisplay(today), p.BloodGroup, p.MaritalStatus, p.Phone, p.AlternatePhone, p.Email, p.Address, p.GuardianName, p.GuardianRelation,
            p.EmergencyContactName, p.EmergencyContactPhone, p.EmergencyContactRelation, p.Occupation, p.Religion, p.Nationality,
            p.PreferredLanguage, p.Category, p.AbhaNumberMasked, p.AbhaAddress, p.AadhaarLast4, p.PhotoFileId, p.RegisteredFacilityId,
            p.RegisteredAt, p.IsVip, p.IsDeceased, p.DeceasedAt, p.MergedIntoPatientId, p.Notes, p.IsActive, p.Version));
    }

    private static async Task<IResult> GetSummary(Guid id, HospitalDbContext db, PatientAccessGuard guard, TimeProvider time, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var summaries = await PatientQueries.SummariesAsync(db, [id], time.Today(), ct);
        return summaries.TryGetValue(id, out var summary) ? TypedResults.Ok(summary) : Error.NotFound(nameof(Patient), id).ToProblem();
    }

    private static async Task<IResult> GetByUhid(string uhid, HospitalDbContext db, PatientAccessGuard guard, TimeProvider time, CancellationToken ct)
    {
        var normalized = uhid.Trim().ToUpperInvariant();
        var id = await db.Set<Patient>().Where(p => p.Uhid == normalized).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (id is null)
        {
            return Error.NotFound(nameof(Patient), normalized).ToProblem();
        }

        return await GetSummary(id.Value, db, guard, time, ct);
    }

    private static async Task<IResult> CheckDuplicates(DuplicateCheckRequest request, PatientRegistrationService registration, CancellationToken ct) =>
        TypedResults.Ok(await registration.FindDuplicatesAsync(request, null, ct));

    private static async Task<IResult> Register(RegisterPatientRequest request, PatientRegistrationService registration, CancellationToken ct) =>
        (await registration.RegisterAsync(request, ct)).ToCreated(p => $"/api/v1/patients/{p.Id}");

    private static async Task<IResult> Update(Guid id, UpdatePatientRequest request, PatientRegistrationService registration, PatientAccessGuard guard, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(id, ct);
        return access.IsFailure ? access.Error!.ToProblem() : (await registration.UpdateAsync(id, request, ct)).ToNoContent();
    }

    private static async Task<IResult> Merge(Guid id, MergePatientRequest request, PatientRegistrationService registration, CancellationToken ct) =>
        (await registration.MergeAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> BreakGlass(Guid id, BreakGlassRequest request, PatientAccessGuard guard, CancellationToken ct) =>
        (await guard.BreakGlassAsync(id, request.Reason.Trim(), ct)).ToOk();
}
