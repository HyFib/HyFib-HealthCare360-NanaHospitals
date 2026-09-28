using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;

/// <summary>/api/v1/dialysis/sessions and /api/v1/dialysis/board — scheduling, pre/intra/post recording, charges.</summary>
internal static class DialysisSessionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/dialysis").WithTags("Dialysis");

        group.MapGet("/board", Board).RequirePermission(SpecialtyCarePermissions.DialysisRead);
        group.MapGet("/sessions", List).RequirePermission(SpecialtyCarePermissions.DialysisRead);
        group.MapGet("/sessions/{id:guid}", Get).RequirePermission(SpecialtyCarePermissions.DialysisRead);
        group.MapPost("/sessions", async (ScheduleDialysisRequest r, DialysisService s, CancellationToken ct) =>
                (await s.ScheduleAsync(r, ct)).ToHttpResult(id => TypedResults.Created($"/api/v1/dialysis/sessions/{id}", new { id })))
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<ScheduleDialysisRequest>();
        group.MapPut("/sessions/{id:guid}/machine", async (Guid id, AssignDialysisMachineRequest r, DialysisService s, CancellationToken ct) =>
                (await s.AssignMachineAsync(id, r.MachineId, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<AssignDialysisMachineRequest>();
        group.MapPost("/sessions/{id:guid}/pre-assessment", async (Guid id, PreDialysisRequest r, DialysisService s, CancellationToken ct) =>
                (await s.RecordPreAssessmentAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<PreDialysisRequest>();
        group.MapPost("/sessions/{id:guid}/start", async (Guid id, StartDialysisRequest r, DialysisService s, CancellationToken ct) =>
                (await s.StartAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<StartDialysisRequest>();
        group.MapPost("/sessions/{id:guid}/readings", async (Guid id, AddDialysisReadingsRequest r, DialysisService s, CancellationToken ct) =>
                (await s.AddReadingsAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<AddDialysisReadingsRequest>();
        group.MapPost("/sessions/{id:guid}/complete", async (Guid id, CompleteDialysisRequest r, DialysisService s, CancellationToken ct) =>
                (await s.CompleteAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<CompleteDialysisRequest>();
        group.MapPost("/sessions/{id:guid}/cancel", async (Guid id, CancelDialysisRequest r, DialysisService s, CancellationToken ct) =>
                (await s.CancelAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions).WithValidation<CancelDialysisRequest>();
        group.MapPost("/sessions/{id:guid}/charges/retry", async (Guid id, DialysisService s, CancellationToken ct) =>
                (await s.RetryChargesAsync(id, ct)).ToOk())
            .RequirePermission(SpecialtyCarePermissions.DialysisSessions);
    }

    /// <summary>Day board: machines grouped by sero category with their sessions, unassigned sessions and patients due but not booked.</summary>
    private static async Task<IResult> Board(DateOnly? date, DialysisShift? shift, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var day = date ?? time.Today();
        var sessions = await Rows(db, db.Set<DialysisSession>().AsNoTracking()
                .Where(s => s.ScheduledDate == day && (shift == null || s.Shift == shift) && s.Status != DialysisSessionStatus.Cancelled))
            .ToListAsync(ct);
        var machines = await db.Set<DialysisMachine>().AsNoTracking().Where(m => m.IsActive)
            .OrderBy(SqlOrdering.Rank<DialysisMachine, SeroStatus>(m => m.SeroDedication)).ThenBy(m => m.Code)
            .ToListAsync(ct);

        var booked = sessions.Where(s => s.Status != DialysisSessionStatus.Missed).Select(s => (s.PatientId, s.Shift)).ToHashSet();
        var profiles = await (
                from d in db.Set<DialysisPatientProfile>().AsNoTracking()
                join p in db.Set<Patient>() on d.PatientId equals p.Id
                where d.IsActive && (shift == null || d.Shift == shift)
                select new { d.PatientId, p.Uhid, p.FullName, d.SeroStatus, d.Shift, d.ScheduleDays, d.SeroTestedOn })
            .ToListAsync(ct);
        var weekday = DialysisPatientProfile.ToWeekday(day.DayOfWeek);
        var due = profiles
            .Where(p => p.ScheduleDays.Contains(weekday) && !booked.Contains((p.PatientId, p.Shift)))
            .OrderBy(p => p.Shift).ThenBy(p => p.FullName)
            .Select(p => new DialysisDuePatient(p.PatientId, p.Uhid, p.FullName, p.SeroStatus, p.Shift, SeroSegregation.IsRescreeningDue(p.SeroTestedOn, time.Today())))
            .ToList();

        return TypedResults.Ok(new DialysisBoard(
            day,
            shift,
            machines.Select(m => new DialysisBoardMachine(m.Id, m.Code, m.Station, m.SeroDedication, m.Status, m.NeedsDisinfection,
                sessions.Where(s => s.MachineId == m.Id).OrderBy(s => s.Shift).ToList())).ToList(),
            sessions.Where(s => s.MachineId is null).OrderBy(s => s.Shift).ThenBy(s => s.PatientName).ToList(),
            due));
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, DateOnly? from, DateOnly? to, DialysisSessionStatus? status, Guid? patientId, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var sessions = db.Set<DialysisSession>().AsNoTracking();
        if (from is not null)
        {
            sessions = sessions.Where(s => s.ScheduledDate >= from);
        }

        if (to is not null)
        {
            sessions = sessions.Where(s => s.ScheduledDate <= to);
        }

        if (status is not null)
        {
            sessions = sessions.Where(s => s.Status == status);
        }

        if (patientId is not null)
        {
            sessions = sessions.Where(s => s.PatientId == patientId);
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            var like = term.ToLikePattern();
            sessions = sessions.Where(s => s.SessionNumber == number
                                           || db.Set<Patient>().Any(p => p.Id == s.PatientId && (p.Uhid == number || EF.Functions.ILike(p.FullName, like))));
        }

        return TypedResults.Ok(await Rows(db, sessions.OrderByDescending(s => s.ScheduledDate)
            .ThenBy(SqlOrdering.Rank<DialysisSession, DialysisShift>(s => s.Shift))).ToPagedResultAsync(page, ct));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var row = await (
                from x in db.Set<DialysisSession>().AsNoTracking()
                join p in db.Set<Patient>() on x.PatientId equals p.Id
                join m in db.Set<DialysisMachine>() on x.MachineId equals m.Id into machines
                from m in machines.DefaultIfEmpty()
                join z in db.Set<Dialyzer>() on x.DialyzerId equals z.Id into dialyzers
                from z in dialyzers.DefaultIfEmpty()
                join d in db.Set<DialysisPatientProfile>() on x.PatientId equals d.PatientId into profiles
                from d in profiles.DefaultIfEmpty()
                where x.Id == id
                select new
                {
                    s = x, p.Uhid, p.FullName, MachineCode = m != null ? m.Code : null, DialyzerSerial = z != null ? z.SerialNumber : null,
                    DialyzerMaxUses = z != null ? (int?)z.MaxUses : null, Sero = d != null ? (SeroStatus?)d.SeroStatus : null,
                    DryWeight = d != null ? (decimal?)d.DryWeightKg : null,
                })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Error.NotFound(nameof(DialysisSession), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(row.s.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(row.s.PatientId, $"Viewed dialysis session {row.s.SessionNumber}", ct);
        var s = row.s;
        return TypedResults.Ok(new DialysisSessionDetail(
            s.Id, s.SessionNumber, s.PatientId, row.Uhid, row.FullName, row.Sero, row.DryWeight, s.VisitId, s.AdmissionId, s.ScheduledDate, s.Shift, s.Status,
            s.MachineId, row.MachineCode, s.PreWeightKg, s.PreSystolic, s.PreDiastolic, s.PrePulse, s.PreTemperatureC, s.TargetUfMl, s.PreAssessedAt,
            s.Readings, s.PostWeightKg, s.PostSystolic, s.PostDiastolic, s.PostPulse, s.UfAchievedMl, s.HeparinLoadingUnits, s.HeparinHourlyUnits,
            s.DialyzerId, row.DialyzerSerial, s.DialyzerUseNumber, row.DialyzerMaxUses, s.Complications, s.ComplicationNotes, s.StartedAt, s.EndedAt,
            s.DurationMinutes, s.PerformedByName, s.CancelReason, s.ChargeStatus, s.ChargeError, s.Version));
    }

    private static IQueryable<DialysisSessionListItem> Rows(HospitalDbContext db, IQueryable<DialysisSession> sessions) =>
        from s in sessions
        join p in db.Set<Patient>() on s.PatientId equals p.Id
        join m in db.Set<DialysisMachine>() on s.MachineId equals m.Id into machines
        from m in machines.DefaultIfEmpty()
        select new DialysisSessionListItem(
            s.Id, s.SessionNumber, s.PatientId, p.Uhid, p.FullName, s.ScheduledDate, s.Shift, s.Status, s.MachineId, m != null ? m.Code : null,
            s.StartedAt, s.EndedAt, s.ChargeStatus);
}
