using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Security;

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
        // Asks for the merge; another person who may merge records approves it from the approvals inbox (202).
        group.MapPost("/{id:guid}/merge", Merge).RequirePermission(PatientsPermissions.Merge).WithValidation<MergePatientRequest>();
        group.MapPost("/{id:guid}/break-glass", BreakGlass).RequirePermission(PatientsPermissions.BreakGlass).WithValidation<BreakGlassRequest>();
        group.MapGet("/{id:guid}/sensitive-categories", GetSensitive).RequirePermission(PatientsPermissions.SensitiveManage);
        group.MapPut("/{id:guid}/sensitive-categories", SetSensitive).RequirePermission(PatientsPermissions.SensitiveManage)
            .WithValidation<SetSensitiveCategoriesRequest>();

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
        PatientAccessGuard access,
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

        // A restricted (VIP) patient's name shows only to those who may open the record (R-3).
        var opened = await access.OpenableAsync([.. result.Items.Where(p => p.IsVip).Select(p => p.Id)], ct);

        var today = time.Today();
        return TypedResults.Ok(result.Map(p => new PatientListItem(
            p.Id, p.Uhid, p.IsVip && !opened.Contains(p.Id) ? PatientNames.Restricted(p.FullName) : p.FullName, p.Gender, p.DateOfBirth, p.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            Phone.Mask(p.Phone), p.Category, p.IsVip, p.IsDeceased, p.RegisteredAt, p.LastVisitAt)));
    }

    private static async Task<IResult> Get(
        Guid id,
        HospitalDbContext db,
        PatientAccessGuard guard,
        PatientContactAccess contacts,
        PatientRecordApprovals records,
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
        var pending = await records.PendingAsync(id, ct);
        return TypedResults.Ok(new PatientDetail(
            p.Id, p.Uhid, p.Title, p.FirstName, p.MiddleName, p.LastName, p.FullName, p.Gender, p.DateOfBirth, p.IsDateOfBirthEstimated,
            p.AgeDisplay(today), p.BloodGroup, p.MaritalStatus, PatientContacts.Phone(p.Phone, full), PatientContacts.Phone(p.AlternatePhone, full),
            PatientContacts.Email(p.Email, full), PatientContacts.Address(p.Address, full), p.GuardianName, p.GuardianRelation,
            p.EmergencyContactName, PatientContacts.Phone(p.EmergencyContactPhone, full), p.EmergencyContactRelation, p.Occupation, p.Religion,
            p.Nationality, p.PreferredLanguage, p.Category, p.AbhaNumberMasked, p.AbhaAddress, p.AadhaarLast4, p.PhotoFileId,
            p.RegisteredFacilityId, p.RegisteredAt, p.IsVip, p.IsDeceased, p.DeceasedAt, p.MergedIntoPatientId, p.Notes, p.IsActive, p.Version,
            ContactsMasked: !full,
            PendingChanges: pending.Count == 0 ? null : pending,
            IdentityCorrectableUntil: await records.CorrectableUntilAsync(p, ct)));
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

    /// <summary>Saves the details at once (204); what waits for a second person is asked for, and the answer is its request (202).</summary>
    private static async Task<IResult> Update(Guid id, UpdatePatientRequest request, PatientRegistrationService registration, PatientAccessGuard guard, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(id, ct);
        return access.IsFailure
            ? access.Error!.ToProblem()
            : (await registration.UpdateAsync(id, request, ct)).ToHttpResult(saved =>
                saved.Asked is { } asked ? TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked) : TypedResults.NoContent());
    }

    private static async Task<IResult> Merge(Guid id, MergePatientRequest request, PatientRegistrationService registration, CancellationToken ct) =>
        (await registration.RequestMergeAsync(id, request, ct)).ToHttpResult(approval => TypedResults.Accepted($"/api/v1/approvals/{approval.Id}", approval));

    private static async Task<IResult> BreakGlass(Guid id, BreakGlassRequest request, PatientAccessGuard guard, CancellationToken ct) =>
        (await guard.BreakGlassAsync(id, request.Reason.Trim(), ct)).ToOk();

    private static async Task<IResult> GetSensitive(Guid id, PatientAccessGuard guard, HospitalDbContext db, PatientRecordApprovals records, CancellationToken ct)
    {
        // Managing the categories is not opening the chart: a doctor who may not open an MTP chart can still correct a
        // category recorded in error. A restricted (VIP) record stays closed.
        var access = await guard.EnsureCanOpenAsync(id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == id).Select(p => new { p.SensitiveCategories, p.Version }).FirstAsync(ct);
        return TypedResults.Ok(await SensitiveAsync(id, patient.SensitiveCategories, patient.Version, records, ct));
    }

    /// <summary>The categories in force, and those asked to be taken out (still in force until approved).</summary>
    private static async Task<SensitiveCategoriesDto> SensitiveAsync(Guid id, SensitiveCategory categories, uint version, PatientRecordApprovals records, CancellationToken ct)
    {
        var waiting = await records.WaitingAsync<SensitiveRemovalPayload>(PatientRecordApprovals.SensitiveKind, id, ct);
        return new SensitiveCategoriesDto(Split(categories), version, waiting is { } w ? Split(w.Payload.Remove) : null, waiting?.Request);
    }

    /// <summary>
    /// A doctor puts the record in sensitive categories at once (R-4), with a reason, audited as a security event. Taking it
    /// out of one waits for another doctor who manages the categories (maker-checker, slice 7): the record keeps the
    /// category meanwhile, and the answer is the request (202).
    /// </summary>
    private static async Task<IResult> SetSensitive(
        Guid id, SetSensitiveCategoriesRequest request, PatientAccessGuard guard, HospitalDbContext db, IAuditLogger audit, PatientRecordApprovals records,
        CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(id, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var patient = await db.Set<Patient>().FirstAsync(p => p.Id == id, ct);
        db.SetExpectedVersion(patient, request.Version);
        var categories = request.Categories.Aggregate(SensitiveCategory.None, (all, c) => all | c);
        var before = patient.SensitiveCategories;
        var added = categories & ~before;
        var removed = before & ~categories;
        if (removed != SensitiveCategory.None)
        {
            var waiting = await records.WaitingAsync<SensitiveRemovalPayload>(PatientRecordApprovals.SensitiveKind, id, ct);
            if (waiting is { } w && w.Payload.Remove != removed)
            {
                return Error.Conflict(PatientRecordApprovals.WaitingCode,
                    $"Taking this record out of {SensitiveCategories.Describe(w.Payload.Remove)}, asked for by {w.Request.RequestedBy}, is waiting for approval. Withdraw it in Approvals first, or wait for its decision.")
                    .ToProblem();
            }

            if (waiting is not null)
            {
                removed = SensitiveCategory.None;
            }
        }

        if (removed != SensitiveCategory.None)
        {
            // Asked for over the categories as the doctor saw them, even when nothing is added.
            db.GuardVersion(patient, request.Version);
        }

        if (added != SensitiveCategory.None)
        {
            patient.SensitiveCategories |= added;
            audit.Add(new AuditEntry(AuditCategories.Security, "patient.sensitive_changed",
                $"Sensitive categories changed from \"{Describe(before)}\" to \"{Describe(patient.SensitiveCategories)}\": {request.Reason.Trim()}")
            {
                PatientId = id,
                EntityType = nameof(Patient),
                EntityId = id.ToString(),
                Severity = AuditSeverity.Warning,
            });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (removed != SensitiveCategory.None)
        {
            var asked = await records.AskSensitiveRemovalAsync(patient, removed, request.Reason, ct);
            if (asked.IsFailure)
            {
                return asked.Error!.ToProblem();
            }

            await transaction.CommitAsync(ct);
            return TypedResults.Accepted($"/api/v1/approvals/{asked.Value.Id}", asked.Value);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Ok(await SensitiveAsync(id, patient.SensitiveCategories, patient.Version, records, ct));
    }

    private static string Describe(SensitiveCategory categories) => categories == SensitiveCategory.None ? "none" : SensitiveCategories.Describe(categories);

    private static IReadOnlyList<SensitiveCategory> Split(SensitiveCategory categories) =>
        [.. Enum.GetValues<SensitiveCategory>().Where(c => c != SensitiveCategory.None && categories.HasFlag(c))];
}
