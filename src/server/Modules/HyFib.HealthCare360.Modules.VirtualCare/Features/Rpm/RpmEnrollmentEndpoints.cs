using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Services;

namespace HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;

/// <summary>/api/v1/rpm — programmes, enrolments, thresholds, readings, trends and adherence (RPM-01..04).</summary>
internal static class RpmEnrollmentEndpoints
{
    private const int MaxTrendDays = 366;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rpm").WithTags("Remote Monitoring");

        group.MapGet("/dashboard", Dashboard).RequirePermission(VirtualCarePermissions.RpmRead);
        group.MapGet("/programs", ListPrograms).RequirePermission(VirtualCarePermissions.RpmRead);
        group.MapPost("/programs", (SaveRpmProgramRequest request, HospitalDbContext db, CancellationToken ct) => SaveProgram(null, request, db, ct))
            .RequirePermission(VirtualCarePermissions.RpmManage).WithValidation<SaveRpmProgramRequest>();
        group.MapPut("/programs/{id:guid}", (Guid id, SaveRpmProgramRequest request, HospitalDbContext db, CancellationToken ct) => SaveProgram(id, request, db, ct))
            .RequirePermission(VirtualCarePermissions.RpmManage).WithValidation<SaveRpmProgramRequest>().RequireVersion();

        group.MapGet("/enrollments", ListEnrollments).RequirePermission(VirtualCarePermissions.RpmRead);
        group.MapGet("/enrollments/{id:guid}", GetEnrollment).RequirePermission(VirtualCarePermissions.RpmRead);
        group.MapPost("/enrollments", Enroll).RequirePermission(VirtualCarePermissions.RpmManage).WithValidation<EnrollPatientRequest>();
        group.MapPut("/enrollments/{id:guid}", UpdateEnrollment).RequirePermission(VirtualCarePermissions.RpmManage).WithValidation<UpdateEnrollmentRequest>();
        group.MapDelete("/enrollments/{id:guid}", DeleteEnrollment).RequirePermission(VirtualCarePermissions.RpmManage);
        group.MapPut("/enrollments/{id:guid}/thresholds", SetThresholds).RequirePermission(VirtualCarePermissions.RpmManage).WithValidation<SetThresholdsRequest>();
        group.MapGet("/enrollments/{id:guid}/readings", ListReadings).RequirePermission(VirtualCarePermissions.RpmRead);
        group.MapPost("/enrollments/{id:guid}/readings", RecordReadings).RequirePermission(VirtualCarePermissions.RpmManage).WithValidation<RecordReadingsRequest>();
        group.MapGet("/enrollments/{id:guid}/trends", Trends).RequirePermission(VirtualCarePermissions.RpmRead);
        group.MapGet("/enrollments/{id:guid}/adherence", GetAdherence).RequirePermission(VirtualCarePermissions.RpmRead);
    }

    private static async Task<IResult> Dashboard(RpmInsights insights, ICurrentUser user, CancellationToken ct) =>
        TypedResults.Ok(await insights.DashboardAsync(user.FacilityId, ct));

    private static async Task<IResult> ListPrograms(bool? activeOnly, HospitalDbContext db, CancellationToken ct)
    {
        var programs = db.Set<RpmProgram>().AsNoTracking();
        if (activeOnly == true)
        {
            programs = programs.Where(p => p.IsActive);
        }

        return TypedResults.Ok(await programs.OrderBy(p => p.Name)
            .Select(p => new RpmProgramDto(p.Id, p.Code, p.Name, p.Kind, p.Description, p.ExpectedReadingsPerDay, p.IsActive, p.Version))
            .ToListAsync(ct));
    }

    private static async Task<IResult> SaveProgram(Guid? id, SaveRpmProgramRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<RpmProgram>().AnyAsync(p => p.Code == code && p.Id != id, ct))
        {
            return Error.Conflict("rpm.program_code_in_use", $"Programme code {code} is already used.").ToProblem();
        }

        RpmProgram program;
        if (id is { } existingId)
        {
            var found = await db.Set<RpmProgram>().FirstOrDefaultAsync(p => p.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(RpmProgram), existingId).ToProblem();
            }

            program = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(program, version);
            }
        }
        else
        {
            program = new RpmProgram();
            db.Add(program);
        }

        program.Code = code;
        program.Name = request.Name.Trim();
        program.Kind = request.Kind;
        program.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        program.ExpectedReadingsPerDay = request.ExpectedReadingsPerDay;
        program.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/rpm/programs/{program.Id}", new { program.Id }) : TypedResults.NoContent();
    }

    /// <summary>Monitoring worklist: patients with critical alerts first, then open alerts, then the most recent readings.</summary>
    private static async Task<IResult> ListEnrollments(
        [AsParameters] PageQuery query,
        RpmEnrollmentStatus? status,
        Guid? programId,
        Guid? practitionerId,
        HospitalDbContext db,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var enrollments = db.Set<RpmEnrollment>().AsNoTracking();
        enrollments = status is { } wanted
            ? enrollments.Where(e => e.Status == wanted)
            : enrollments.Where(e => e.Status == RpmEnrollmentStatus.Active || e.Status == RpmEnrollmentStatus.Paused);

        if (programId is not null)
        {
            enrollments = enrollments.Where(e => e.ProgramId == programId);
        }

        if (practitionerId is { } member)
        {
            enrollments = enrollments.Where(e => e.CareTeamPractitionerIds.Contains(member));
        }

        if (page.SearchTerm is { } term)
        {
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            enrollments = enrollments.Where(e => matching.Contains(e.PatientId));
        }

        var alerts = db.Set<RpmAlert>().Where(a => a.Status == RpmAlertStatus.Open || a.Status == RpmAlertStatus.Acknowledged);
        var result = await (
                from e in enrollments
                join p in db.Set<Patient>() on e.PatientId equals p.Id
                join pg in db.Set<RpmProgram>() on e.ProgramId equals pg.Id
                let open = alerts.Count(a => a.EnrollmentId == e.Id)
                let critical = alerts.Count(a => a.EnrollmentId == e.Id && a.Severity == RpmAlertSeverity.Critical)
                orderby critical descending, open descending, e.LastReadingAt descending
                select new
                {
                    e.Id,
                    e.PatientId,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    e.ProgramId,
                    ProgramName = pg.Name,
                    pg.Kind,
                    e.Status,
                    e.StartDate,
                    e.EndDate,
                    e.LastReadingAt,
                    Open = open,
                    Critical = critical,
                })
            .ToPagedResultAsync(page, ct);

        var today = time.Today();
        return TypedResults.Ok(result.Map(r => new RpmEnrollmentListItem(
            r.Id, r.PatientId, r.Uhid, r.FullName, r.Gender, r.DateOfBirth is { } dob ? Age.Format(dob, today) : null, r.ProgramId, r.ProgramName,
            r.Kind, r.Status, r.StartDate, r.EndDate, r.LastReadingAt, r.Open, r.Critical)));
    }

    private static async Task<IResult> GetEnrollment(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, VirtualCareSettings settings, TimeProvider time, CancellationToken ct)
    {
        var row = await (
                from e in db.Set<RpmEnrollment>().AsNoTracking()
                join p in db.Set<Patient>() on e.PatientId equals p.Id
                join pg in db.Set<RpmProgram>() on e.ProgramId equals pg.Id
                where e.Id == id
                select new { Enrollment = e, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, p.Phone, ProgramName = pg.Name, pg.Kind })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Error.NotFound(nameof(RpmEnrollment), id).ToProblem();
        }

        var enrollment = row.Enrollment;
        var access = await guard.EnsureCanOpenAsync(enrollment.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(enrollment.PatientId, "Viewed remote monitoring enrolment", ct);

        var teamIds = enrollment.CareTeamPractitionerIds;
        var team = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => teamIds.Contains(p.Id))
            .Select(p => new { p.Id, p.DisplayName })
            .ToListAsync(ct);
        var thresholds = await db.Set<RpmThreshold>().AsNoTracking().Where(t => t.EnrollmentId == id).ToListAsync(ct);
        var devices = await db.Set<RpmDevice>().AsNoTracking().Where(d => d.EnrollmentId == id).OrderBy(d => d.SerialNumber).ToListAsync(ct);
        var openAlerts = await db.Set<RpmAlert>().CountAsync(a => a.EnrollmentId == id && (a.Status == RpmAlertStatus.Open || a.Status == RpmAlertStatus.Acknowledged), ct);

        return TypedResults.Ok(new RpmEnrollmentDetail(
            enrollment.Id, enrollment.FacilityId, enrollment.PatientId, row.Uhid, row.FullName, row.Gender,
            row.DateOfBirth is { } dob ? Age.Format(dob, time.Today()) : null, Phone.Mask(row.Phone), enrollment.ProgramId, row.ProgramName, row.Kind,
            enrollment.Status, enrollment.StatusReason, enrollment.StartDate, enrollment.EndDate, enrollment.Goals, enrollment.Notes,
            teamIds.Select(tid => new CareTeamMember(tid, team.FirstOrDefault(t => t.Id == tid)?.DisplayName ?? "Clinician", tid == enrollment.PrimaryPractitionerId)).ToList(),
            thresholds.OrderBy(t => t.Type).Select(t => ThresholdDto.From(t.Type, t.Range)).ToList(),
            devices.Select(RpmDeviceEndpoints.ToDto).ToList(),
            enrollment.LastReadingAt, enrollment.ThresholdsUpdatedAt, openAlerts, enrollment.Version));
    }

    private static async Task<IResult> Enroll(EnrollPatientRequest request, RpmEnrollmentService enrollments, CancellationToken ct)
    {
        var result = await enrollments.EnrollAsync(request, ct);
        return result.IsFailure
            ? result.Error!.ToProblem()
            : TypedResults.Created($"/api/v1/rpm/enrollments/{result.Value.Id}", new { result.Value.Id });
    }

    private static async Task<IResult> UpdateEnrollment(Guid id, UpdateEnrollmentRequest request, RpmEnrollmentService enrollments, CancellationToken ct) =>
        (await enrollments.UpdateAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> DeleteEnrollment(Guid id, RpmEnrollmentService enrollments, CancellationToken ct) =>
        (await enrollments.DeleteAsync(id, ct)).ToNoContent();

    private static async Task<IResult> SetThresholds(Guid id, SetThresholdsRequest request, RpmEnrollmentService enrollments, CancellationToken ct) =>
        (await enrollments.SetThresholdsAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> ListReadings(
        Guid id,
        [AsParameters] PageQuery query,
        RpmReadingType? type,
        DateOnly? from,
        DateOnly? to,
        HospitalDbContext db,
        PatientAccessGuard guard,
        CancellationToken ct)
    {
        var patientId = await db.Set<RpmEnrollment>().Where(e => e.Id == id).Select(e => (Guid?)e.PatientId).FirstOrDefaultAsync(ct);
        if (patientId is null)
        {
            return Error.NotFound(nameof(RpmEnrollment), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(patientId.Value, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var readings = db.Set<RpmReading>().AsNoTracking().Where(r => r.EnrollmentId == id);
        if (type is not null)
        {
            readings = readings.Where(r => r.Type == type);
        }

        var calendar = HospitalCalendar.India;
        if (from is { } start)
        {
            var since = calendar.StartOfDay(start);
            readings = readings.Where(r => r.MeasuredAt >= since);
        }

        if (to is { } end)
        {
            var until = calendar.StartOfDay(end.AddDays(1));
            readings = readings.Where(r => r.MeasuredAt < until);
        }

        return TypedResults.Ok(await readings
            .OrderByDescending(r => r.MeasuredAt)
            .Select(r => new RpmReadingDto(r.Id, r.Type, r.Value, r.Unit, r.TextValue, r.MeasuredAt, r.ReceivedAt, r.Severity, r.Source, r.DeviceId))
            .ToPagedResultAsync(query.ToRequest(), ct));
    }

    /// <summary>Readings entered by a nurse (for example read out by the patient on a follow-up call); evaluated like device readings.</summary>
    private static async Task<IResult> RecordReadings(
        Guid id,
        RecordReadingsRequest request,
        HospitalDbContext db,
        RpmIngestionService ingestion,
        ICurrentUser user,
        CancellationToken ct)
    {
        var enrollment = await db.Set<RpmEnrollment>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (enrollment is null)
        {
            return Error.NotFound(nameof(RpmEnrollment), id).ToProblem();
        }

        var inputs = request.Readings.Select(r => new ReadingInput(r.Type, r.Value, r.Unit, r.MeasuredAt, r.TextValue)).ToList();
        return (await ingestion.IngestAsync(enrollment, null, inputs, ReadingSource.Manual, user.UserId, ct)).ToOk();
    }

    /// <param name="types">Comma-separated reading types (for example <c>SystolicBp,DiastolicBp</c>); empty = all.</param>
    private static async Task<IResult> Trends(
        Guid id,
        string? types,
        DateOnly? from,
        DateOnly? to,
        HospitalDbContext db,
        RpmInsights insights,
        PatientAccessGuard guard,
        TimeProvider time,
        CancellationToken ct)
    {
        var enrollment = await db.Set<RpmEnrollment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (enrollment is null)
        {
            return Error.NotFound(nameof(RpmEnrollment), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(enrollment.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var until = to ?? time.Today();
        var since = from ?? until.AddDays(-29);
        if (until < since || until.DayNumber - since.DayNumber >= MaxTrendDays)
        {
            return Error.Field("to", "Choose a range of up to one year.").ToProblem();
        }

        var wanted = new List<RpmReadingType>();
        foreach (var name in (types ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<RpmReadingType>(name, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Error.Field("types", $"Unknown reading type '{name}'.").ToProblem();
            }

            wanted.Add(parsed);
        }

        return TypedResults.Ok(await insights.TrendsAsync(enrollment, wanted, since, until, ct));
    }

    private static async Task<IResult> GetAdherence(Guid id, int? days, HospitalDbContext db, RpmInsights insights, PatientAccessGuard guard, CancellationToken ct)
    {
        var enrollment = await db.Set<RpmEnrollment>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (enrollment is null)
        {
            return Error.NotFound(nameof(RpmEnrollment), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(enrollment.PatientId, ct);
        return access.IsFailure
            ? access.Error!.ToProblem()
            : TypedResults.Ok(await insights.AdherenceAsync(enrollment, days ?? Adherence.DefaultWindowDays, ct));
    }
}
