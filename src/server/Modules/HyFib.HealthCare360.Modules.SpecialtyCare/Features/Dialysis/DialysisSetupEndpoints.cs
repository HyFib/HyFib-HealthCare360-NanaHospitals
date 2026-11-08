using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;

/// <summary>/api/v1/dialysis — machine/station master, patient enrolment (dialysis profile) and dialyzers.</summary>
internal static class DialysisSetupEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/dialysis").WithTags("Dialysis");

        group.MapGet("/machines", ListMachines).RequirePermission(SpecialtyCarePermissions.DialysisRead);
        group.MapPost("/machines", (SaveDialysisMachineRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveMachine(null, r, db, u, ct))
            .RequirePermission(SpecialtyCarePermissions.DialysisMachines).WithValidation<SaveDialysisMachineRequest>();
        group.MapPut("/machines/{id:guid}", (Guid id, SaveDialysisMachineRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveMachine(id, r, db, u, ct))
            .RequirePermission(SpecialtyCarePermissions.DialysisMachines).WithValidation<SaveDialysisMachineRequest>().RequireVersion();
        group.MapPost("/machines/{id:guid}/disinfection", async (Guid id, RecordDisinfectionRequest r, DialysisService s, CancellationToken ct) =>
                (await s.RecordDisinfectionAsync(id, r.Method, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<RecordDisinfectionRequest>();

        group.MapGet("/patients", ListProfiles).RequirePermission(SpecialtyCarePermissions.DialysisRead);
        group.MapGet("/patients/{patientId:guid}", GetProfile).RequirePermission(SpecialtyCarePermissions.DialysisRead);
        group.MapPut("/patients/{patientId:guid}", async (Guid patientId, SaveDialysisProfileRequest r, DialysisService s, CancellationToken ct) =>
                (await s.SaveProfileAsync(patientId, r, ct)).ToHttpResult(id => TypedResults.Ok(new { id })))
            .RequirePermission(SpecialtyCarePermissions.DialysisPatients).WithValidation<SaveDialysisProfileRequest>().FirstSaveCreates();

        // Off a positive status waits for a second person (202); negative to positive isolates at once (204). Both need the lab result.
        group.MapPost("/patients/{patientId:guid}/infection-status", async (Guid patientId, ChangeInfectionStatusRequest r, InfectionStatusService s, CancellationToken ct) =>
                (await s.ChangeAsync(patientId, r, ct)).ToHttpResult(o => o.Asked is { } asked ? TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked) : TypedResults.NoContent()))
            .RequirePermission(SpecialtyCarePermissions.DialysisPatients).WithValidation<ChangeInfectionStatusRequest>();

        group.MapGet("/patients/{patientId:guid}/infection-results", InfectionResults).RequirePermission(SpecialtyCarePermissions.DialysisRead);
        group.MapGet("/patients/{patientId:guid}/dialyzers", ListDialyzers).RequirePermission(SpecialtyCarePermissions.DialysisRead);
        group.MapPost("/patients/{patientId:guid}/dialyzers", async (Guid patientId, RegisterDialyzerRequest r, DialysisService s, CancellationToken ct) =>
                (await s.RegisterDialyzerAsync(patientId, r, ct)).ToHttpResult(d => TypedResults.Created($"/api/v1/dialysis/patients/{patientId}/dialyzers", ToDto(d))))
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<RegisterDialyzerRequest>();
        group.MapPost("/dialyzers/{id:guid}/discard", async (Guid id, DialysisReasonRequest r, DialysisService s, CancellationToken ct) =>
                (await s.DiscardDialyzerAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<DialysisReasonRequest>();
    }

    private static async Task<IResult> ListMachines(bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var machines = await db.Set<DialysisMachine>().AsNoTracking()
            .Where(m => includeInactive == true || m.IsActive)
            .OrderBy(SqlOrdering.Rank<DialysisMachine, SeroStatus>(m => m.SeroDedication)).ThenBy(m => m.Code)
            .ToListAsync(ct);
        return TypedResults.Ok(machines.Select(ToDto).ToList());
    }

    private static async Task<IResult> SaveMachine(Guid? id, SaveDialysisMachineRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<DialysisMachine>().AnyAsync(m => m.Code == code && m.Id != id, ct))
        {
            return Error.Conflict("dialysis.machine_code_in_use", $"Machine code {code} is already used.").ToProblem();
        }

        DialysisMachine machine;
        if (id is { } existingId)
        {
            var found = await db.Set<DialysisMachine>().FirstOrDefaultAsync(m => m.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(DialysisMachine), existingId).ToProblem();
            }

            // Re-dedicating a machine to another sero category requires it to be idle and disinfected first.
            if (found.SeroDedication != request.SeroDedication && (found.Status == DialysisMachineStatus.InUse || found.NeedsDisinfection))
            {
                return Error.Rule("dialysis.rededication_blocked", "Disinfect the machine (and end any running session) before changing its sero dedication.").ToProblem();
            }

            machine = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(machine, version);
            }
        }
        else
        {
            if (user.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.").ToProblem();
            }

            machine = new DialysisMachine { FacilityId = facilityId };
            db.Add(machine);
        }

        if (request.Status == DialysisMachineStatus.InUse && machine.Status != DialysisMachineStatus.InUse)
        {
            return Error.Field("status", "A machine becomes 'in use' only by starting a session.").ToProblem();
        }

        machine.Code = code;
        machine.Model = request.Model.Trim();
        machine.Manufacturer = string.IsNullOrWhiteSpace(request.Manufacturer) ? null : request.Manufacturer.Trim();
        machine.SerialNumber = string.IsNullOrWhiteSpace(request.SerialNumber) ? null : request.SerialNumber.Trim();
        machine.Station = string.IsNullOrWhiteSpace(request.Station) ? null : request.Station.Trim();
        machine.SeroDedication = request.SeroDedication;
        machine.Status = machine.Status == DialysisMachineStatus.InUse ? DialysisMachineStatus.InUse : request.Status;
        machine.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/dialysis/machines/{machine.Id}", new { machine.Id }) : TypedResults.NoContent();
    }

    private static async Task<IResult> ListProfiles([AsParameters] PageQuery query, bool? includeInactive, SeroStatus? sero, HospitalDbContext db, IFieldProtector protector,
        TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var profiles = db.Set<DialysisPatientProfile>().AsNoTracking().Where(p => includeInactive == true || p.IsActive);
        if (sero is not null)
        {
            profiles = profiles.Where(p => p.SeroStatus == sero);
        }

        if (page.SearchTerm is { } term)
        {
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            profiles = profiles.Where(p => matching.Contains(p.PatientId));
        }

        var result = await (
                from d in profiles
                join p in db.Set<Patient>() on d.PatientId equals p.Id
                orderby p.FullName
                select new { d, p.Uhid, p.FullName, p.Gender, p.DateOfBirth })
            .ToPagedResultAsync(page, ct);

        var today = time.Today();
        var names = await SpecialtyLookups.PractitionerNamesAsync(db, result.Items.Select(r => r.d.NephrologistId), ct);
        return TypedResults.Ok(result.Map(r => ToDto(r.d, r.Uhid, r.FullName, r.Gender, r.DateOfBirth, names, today)));
    }

    private static async Task<IResult> GetProfile(Guid patientId, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenChartAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var row = await (
                from d in db.Set<DialysisPatientProfile>().AsNoTracking()
                join p in db.Set<Patient>() on d.PatientId equals p.Id
                where d.PatientId == patientId
                select new { d, p.Uhid, p.FullName, p.Gender, p.DateOfBirth })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Error.NotFound(nameof(DialysisPatientProfile), patientId).ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, "Viewed dialysis profile", ct);
        var names = await SpecialtyLookups.PractitionerNamesAsync(db, [row.d.NephrologistId], ct);
        var now = time.GetUtcNow();
        var pending = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(a => a.Kind == InfectionStatusService.ChangeKind && a.SubjectType == nameof(DialysisPatientProfile) && a.SubjectId == row.d.Id
                        && a.Status == ApprovalStatus.Pending && a.ExpiresAt > now)
            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
        return TypedResults.Ok(ToDto(row.d, row.Uhid, row.FullName, row.Gender, row.DateOfBirth, names, time.Today()) with { InfectionStatusPendingApprovalId = pending });
    }

    /// <summary>The patient's verified HBsAg, HCV and HIV results of the last 90 days: those an infection status may change on.</summary>
    private static async Task<IResult> InfectionResults(Guid patientId, HospitalDbContext db, PatientAccessGuard guard, TimeProvider time, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenChartAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var since = time.GetUtcNow().AddDays(-InfectionStatusChange.MaxResultAgeDays - 1);
        var results = await db.Set<LabResult>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(r => r.PatientId == patientId && r.Status == LabResultStatus.Verified && r.VerifiedAt >= since)
            .OrderByDescending(r => r.VerifiedAt).Take(200)
            .Select(r => new { r.Id, r.TestCode, r.TestName, r.ValueText, r.NumericValue, r.Flag, r.VerifiedAt })
            .ToListAsync(ct);
        var today = time.Today();
        return TypedResults.Ok(results
            .Select(r => new
            {
                r,
                Infection = new[] { SeroStatus.HBsAg, SeroStatus.HCV, SeroStatus.HIV }.Cast<SeroStatus?>().FirstOrDefault(s => InfectionStatusChange.TestsFor(s!.Value, r.TestCode, r.TestName)),
                On = HospitalClock.ToLocalDate(r.VerifiedAt!.Value),
            })
            .Where(x => x.Infection is not null && InfectionStatusChange.Recent(x.On, today))
            .Select(x => new InfectionResultDto(x.r.Id, x.Infection!.Value, x.r.TestName, x.r.ValueText ?? x.r.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                x.r.Flag?.ToString(), x.On))
            .ToList());
    }

    private static async Task<IResult> ListDialyzers(Guid patientId, bool? includeDiscarded, HospitalDbContext db, CancellationToken ct)
    {
        var dialyzers = await db.Set<Dialyzer>().AsNoTracking()
            .Where(d => d.PatientId == patientId && (includeDiscarded == true || !d.IsDiscarded))
            .OrderByDescending(d => d.LastUsedAt ?? d.CreatedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(dialyzers.Select(ToDto).ToList());
    }

    internal static DialysisMachineDto ToDto(DialysisMachine m) => new(
        m.Id, m.Code, m.Model, m.Manufacturer, m.SerialNumber, m.Station, m.SeroDedication, m.Status, m.LastDisinfectedAt, m.LastDisinfectionMethod,
        m.LastUsedAt, m.NeedsDisinfection, m.IsActive, m.Version);

    internal static DialyzerDto ToDto(Dialyzer d) => new(
        d.Id, d.PatientId, d.SerialNumber, d.Brand, d.Model, d.SurfaceAreaM2, d.UsesCount, d.MaxUses, d.RemainingUses, d.FirstUsedAt, d.LastUsedAt,
        d.IsDiscarded, d.DiscardedAt, d.DiscardReason);

    private static DialysisProfileDto ToDto(
        DialysisPatientProfile d, string uhid, string name, Gender gender, DateOnly? dateOfBirth, IReadOnlyDictionary<Guid, string> names, DateOnly today) => new(
        d.Id, d.PatientId, uhid, name, gender, dateOfBirth is { } dob ? Age.Format(dob, today) : null, d.DryWeightKg, d.VascularAccess, d.AccessSite,
        d.AccessCreatedOn, d.ScheduleDays, d.Shift, d.SessionMinutes, d.SeroStatus, d.SeroTestedOn, SeroSegregation.IsRescreeningDue(d.SeroTestedOn, today),
        d.ReusePolicy, d.MaxDialyzerUses, d.EffectiveMaxDialyzerUses, d.NephrologistId, d.NephrologistId is { } n ? names.GetValueOrDefault(n) : null,
        d.PrimaryDiagnosis, d.AnticoagulationNotes, d.EnrolledOn, d.IsActive, d.Version)
    {
        SeroChangedByName = d.SeroChangedByName,
        SeroChangeRequestedByName = d.SeroChangeRequestedByName,
        SeroLabResultId = d.SeroLabResultId,
    };
}
