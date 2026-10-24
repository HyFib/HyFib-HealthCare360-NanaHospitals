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
        group.MapPost("/duplicates", CheckDuplicates).RequirePermission(PatientsPermissions.Register).WithValidation<DuplicateCheckRequest>();
        group.MapPost("/", Register).RequirePermission(PatientsPermissions.Register).WithValidation<RegisterPatientRequest>();
        // Who edits a patient's details sees them in full first (SRS PRV-03): a masked number saved back would replace the real one.
        group.MapPut("/{id:guid}", Update).RequirePermission(PatientsPermissions.Register).RequirePermission(PatientsPermissions.ContactRead)
            .WithValidation<UpdatePatientRequest>();
        group.MapPost("/{id:guid}/merge", Merge).RequirePermission(PatientsPermissions.Merge).WithValidation<MergePatientRequest>();
        group.MapPost("/{id:guid}/break-glass", BreakGlass).RequirePermission(PatientsPermissions.BreakGlass).WithValidation<BreakGlassRequest>();

        // Patient Portal: the signed-in patient's own record and family members sharing the verified mobile number.
        app.MapGet("/api/v1/portal/profiles", PortalProfiles).WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal)
            .RequireFeature(FeatureKeys.PatientPortal);
    }

    private static async Task<IResult> PortalProfiles(PortalPatientScope scope, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        // The patient's own records and their family's, which share the patient's verified mobile number.
        var ids = await scope.AccessiblePatientIdsAsync(ct);
        var summaries = await PatientQueries.SummariesAsync(db, ids, time.Today(), fullContacts: true, ct);
        return TypedResults.Ok(ids.Where(summaries.ContainsKey).Select(id => summaries[id]).ToList());
    }

    private static async Task<IResult> Search(
        [AsParameters] PageQuery query,
        Gender? gender,
        PatientCategory? category,
        bool? includeInactive,
        HospitalDbContext db,
        IFieldProtector protector,
        IAuditLogger audit,
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

        // A search shows names, UHIDs and ages: who searched for what, and whom they were shown, is PHI access (A-1).
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "patient.searched",
            page.SearchTerm is null ? "Listed patients" : $"Searched patients for \"{page.SearchTerm}\"")
        {
            Details = new { term = page.SearchTerm, gender, category, includeInactive, page = page.Page, matched = result.TotalCount, shown = result.Items.Select(p => p.Id) },
        }, ct);

        var today = time.Today();
        return TypedResults.Ok(result.Map(p => new PatientListItem(
            p.Id, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, p.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            Phone.Mask(p.Phone), p.Category, p.IsVip, p.IsDeceased, p.RegisteredAt, p.LastVisitAt)));
    }

    private static async Task<IResult> Get(
        Guid id,
        HospitalDbContext db,
        PatientAccessGuard guard,
        PatientContactAccess contacts,
        IAuditLogger audit,
        TimeProvider time,
        CancellationToken ct)
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
        var full = await contacts.FullAsync(ct);
        return TypedResults.Ok(new PatientDetail(
            p.Id, p.Uhid, p.Title, p.FirstName, p.MiddleName, p.LastName, p.FullName, p.Gender, p.DateOfBirth, p.IsDateOfBirthEstimated,
            p.AgeDisplay(today), p.BloodGroup, p.MaritalStatus, PatientContacts.Phone(p.Phone, full), PatientContacts.Phone(p.AlternatePhone, full),
            PatientContacts.Email(p.Email, full), PatientContacts.Address(p.Address, full), p.GuardianName, p.GuardianRelation,
            p.EmergencyContactName, PatientContacts.Phone(p.EmergencyContactPhone, full), p.EmergencyContactRelation, p.Occupation, p.Religion,
            p.Nationality, p.PreferredLanguage, p.Category, p.AbhaNumberMasked, p.AbhaAddress, p.AadhaarLast4, p.PhotoFileId,
            p.RegisteredFacilityId, p.RegisteredAt, p.IsVip, p.IsDeceased, p.DeceasedAt, p.MergedIntoPatientId, p.Notes, p.IsActive, p.Version,
            ContactsMasked: !full));
    }

    private static Task<IResult> GetSummary(
        Guid id,
        HospitalDbContext db,
        PatientAccessGuard guard,
        PatientContactAccess contacts,
        IAuditLogger audit,
        TimeProvider time,
        CancellationToken ct) =>
        Summary(id, "Viewed patient summary", db, guard, contacts, audit, time, ct);

    /// <summary>The patient's summary (the banner), recorded as access to the patient's record like the full record is.</summary>
    private static async Task<IResult> Summary(
        Guid id,
        string what,
        HospitalDbContext db,
        PatientAccessGuard guard,
        PatientContactAccess contacts,
        IAuditLogger audit,
        TimeProvider time,
        CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var summaries = await PatientQueries.SummariesAsync(db, [id], time.Today(), await contacts.FullAsync(ct), ct);
        if (!summaries.TryGetValue(id, out var summary))
        {
            return Error.NotFound(nameof(Patient), id).ToProblem();
        }

        await audit.PatientRecordAccessedAsync(id, what, ct);
        return TypedResults.Ok(summary);
    }

    private static async Task<IResult> GetByUhid(
        string uhid,
        HospitalDbContext db,
        PatientAccessGuard guard,
        PatientContactAccess contacts,
        IAuditLogger audit,
        TimeProvider time,
        CancellationToken ct)
    {
        var normalized = uhid.Trim().ToUpperInvariant();
        var id = await db.Set<Patient>().Where(p => p.Uhid == normalized).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (id is null)
        {
            return Error.NotFound(nameof(Patient), normalized).ToProblem();
        }

        return await Summary(id.Value, $"Looked up patient by UHID {normalized}", db, guard, contacts, audit, time, ct);
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
