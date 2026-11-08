using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using Npgsql;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.CarePlan;

/// <summary>
/// /api/v1/nursing — the basic nursing care plan of a stay: problems from a short list, each with a goal and
/// interventions with their frequency, evaluated once each shift and resolved when done. All <c>nursing.chart</c>.
/// </summary>
internal static class CarePlanEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/nursing").WithTags("Nursing");

        group.MapGet("/care-plan/problems", () => TypedResults.Ok(CarePlanProblems.All)).RequirePermission(InpatientPermissions.NursingChart);
        group.MapGet("/admissions/{admissionId:guid}/care-plan", List).RequirePermission(InpatientPermissions.NursingChart);
        group.MapPost("/admissions/{admissionId:guid}/care-plan", Add).RequirePermission(InpatientPermissions.NursingChart).WithValidation<AddCarePlanProblemRequest>();
        group.MapPost("/care-plan/{id:guid}/evaluate", Evaluate).RequirePermission(InpatientPermissions.NursingChart).WithValidation<EvaluateCarePlanProblemRequest>();
        group.MapPost("/care-plan/{id:guid}/resolve", Resolve).RequirePermission(InpatientPermissions.NursingChart).WithValidation<ResolveCarePlanProblemRequest>();
    }

    private static async Task<IResult> List(Guid admissionId, bool? activeOnly, AdmissionAccess admissions, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForReadAsync(admissionId, "Viewed the nursing care plan", ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        return TypedResults.Ok(await CarePlanQueries.ForAdmissionsAsync(db, [admissionId], activeOnly ?? false, time.GetUtcNow(), ct));
    }

    private static async Task<IResult> Add(
        Guid admissionId, AddCarePlanProblemRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var access = await admissions.OpenForChartingAsync(admissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var admission = access.Value;
        var template = CarePlanProblems.Find(request.ProblemCode);
        if (template is not null && await db.Set<CarePlanProblem>().AnyAsync(
                p => p.AdmissionId == admissionId && p.ProblemCode == template.Code && p.Status == CarePlanStatus.Active, ct))
        {
            return Error.Conflict("care_plan.duplicate", $"\"{template.Title}\" is already on the care plan.").ToProblem();
        }

        var now = time.GetUtcNow();
        var problem = new CarePlanProblem
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            ProblemCode = template?.Code ?? CarePlanProblems.Other,
            Title = template?.Title ?? request.Title!.Trim(),
            Goal = string.IsNullOrWhiteSpace(request.Goal) ? template!.Goal : request.Goal.Trim(),
            Interventions = request.Interventions is { Count: > 0 } given
                ? given.Select(i => new CarePlanIntervention(i.Text.Trim(), i.Frequency.Trim())).ToList()
                : template!.Interventions,
            Status = CarePlanStatus.Active,
            StartedAt = now,
            StartedBy = user.UserId,
            StartedByName = user.DisplayName,
        };
        db.Add(problem);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/nursing/admissions/{admissionId}/care-plan",
            (await CarePlanQueries.ForAdmissionsAsync(db, [admissionId], false, now, ct)).Single(p => p.Id == problem.Id));
    }

    private static async Task<IResult> Evaluate(
        Guid id, EvaluateCarePlanProblemRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var problem = await db.Set<CarePlanProblem>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (problem is null)
        {
            return Error.NotFound("Care-plan problem", id).ToProblem();
        }

        var access = await admissions.OpenForChartingAsync(problem.AdmissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        if (problem.Status == CarePlanStatus.Resolved)
        {
            return Error.Rule("care_plan.resolved", "This problem is resolved; it is no longer evaluated.").ToProblem();
        }

        var now = time.GetUtcNow();
        db.Add(new CarePlanEvaluation
        {
            FacilityId = problem.FacilityId,
            ProblemId = problem.Id,
            AdmissionId = problem.AdmissionId,
            DutyDate = NursingShifts.DutyDate(now),
            Shift = NursingShifts.At(now),
            Progress = request.Progress,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            EvaluatedAt = now,
            EvaluatedBy = user.UserId,
            EvaluatedByName = user.DisplayName,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("care_plan.evaluated", "This problem is already evaluated for this shift.").ToProblem();
        }

        return TypedResults.Ok((await CarePlanQueries.ForAdmissionsAsync(db, [problem.AdmissionId], false, now, ct)).Single(p => p.Id == id));
    }

    private static async Task<IResult> Resolve(
        Guid id, ResolveCarePlanProblemRequest request, AdmissionAccess admissions, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var problem = await db.Set<CarePlanProblem>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (problem is null)
        {
            return Error.NotFound("Care-plan problem", id).ToProblem();
        }

        var access = await admissions.OpenForChartingAsync(problem.AdmissionId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        if (request.Version != problem.Version)
        {
            return Error.ConcurrencyConflict.ToProblem();
        }

        var now = time.GetUtcNow();
        var resolved = problem.Resolve(user.DisplayName, request.Note, now);
        if (resolved.IsFailure)
        {
            return resolved.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await CarePlanQueries.ForAdmissionsAsync(db, [problem.AdmissionId], false, now, ct)).Single(p => p.Id == id));
    }
}

/// <summary>Reads care plans for the chart, the handover sheet and the downtime print.</summary>
internal static class CarePlanQueries
{
    public static async Task<List<CarePlanProblemDto>> ForAdmissionsAsync(
        HospitalDbContext db, IReadOnlyCollection<Guid> admissionIds, bool activeOnly, DateTimeOffset now, CancellationToken ct)
    {
        var problems = await db.Set<CarePlanProblem>().AsNoTracking()
            .Where(p => admissionIds.Contains(p.AdmissionId) && (!activeOnly || p.Status == CarePlanStatus.Active))
            .OrderBy(p => p.Status).ThenBy(p => p.StartedAt)
            .ToListAsync(ct);
        var ids = problems.Select(p => p.Id).ToList();
        var latest = (await db.Set<CarePlanEvaluation>().AsNoTracking()
                .Where(e => ids.Contains(e.ProblemId))
                .GroupBy(e => e.ProblemId)
                .Select(g => g.OrderByDescending(e => e.EvaluatedAt).First())
                .ToListAsync(ct))
            .ToDictionary(e => e.ProblemId);
        var dutyDate = NursingShifts.DutyDate(now);
        var shift = NursingShifts.At(now);
        return problems.Select(p =>
        {
            var last = latest.GetValueOrDefault(p.Id);
            return new CarePlanProblemDto(p.Id, p.AdmissionId, p.ProblemCode, p.Title, p.Goal, p.Interventions, p.Status, p.StartedAt, p.StartedByName,
                p.ResolvedAt, p.ResolvedByName, p.ResolutionNote,
                last is null ? null : new CarePlanEvaluationDto(last.Id, last.DutyDate, last.Shift, last.Progress, last.Note, last.EvaluatedAt, last.EvaluatedByName),
                last is not null && last.DutyDate == dutyDate && last.Shift == shift, p.Version);
        }).ToList();
    }
}
