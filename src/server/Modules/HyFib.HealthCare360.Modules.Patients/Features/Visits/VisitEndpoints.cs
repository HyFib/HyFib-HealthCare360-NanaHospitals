using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Visits;

/// <summary>/api/v1/visits — check-in, visit lists and visit lifecycle.</summary>
internal static class VisitEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/visits").WithTags("Visits");

        group.MapGet("/", List).RequirePermission(PatientsPermissions.VisitsRead);
        group.MapGet("/{id:guid}", Get).RequirePermission(PatientsPermissions.VisitsRead);
        group.MapPost("/", Create).RequirePermission(PatientsPermissions.VisitsManage).WithValidation<CreateVisitRequest>();
        group.MapPost("/{id:guid}/cancel", Cancel).RequirePermission(PatientsPermissions.VisitsManage).WithValidation<CancelVisitRequest>();
        group.MapPost("/{id:guid}/complete", Complete).RequirePermission(PatientsPermissions.VisitsManage);

        app.MapGet("/api/v1/patients/{patientId:guid}/visits", ForPatient)
            .WithTags("Visits")
            .RequirePermission(PatientsPermissions.VisitsRead);

        app.MapGet("/api/v1/patients/{patientId:guid}/payer", PayerForPatient)
            .WithTags("Visits")
            .RequirePermission(PatientsPermissions.VisitsRead);
    }

    /// <summary>
    /// The visits of a day or a range, with a search: a restricted (VIP) patient the user may not open is named by initials,
    /// as patient search names them (<see cref="VisitPrivacy"/>), and the list is recorded as a patient search is.
    /// </summary>
    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        VisitType? type,
        VisitStatus? status,
        Guid? departmentId,
        Guid? practitionerId,
        HospitalDbContext db,
        IFieldProtector protector,
        IAuditLogger audit,
        PatientAccessGuard access,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.Today();
        var (first, last) = (from ?? today, to ?? from ?? today);
        var start = ToUtcStart(first);
        var end = ToUtcStart(last.AddDays(1));

        var visits = db.Set<Visit>().AsNoTracking().Where(v => v.VisitedAt >= start && v.VisitedAt < end);
        if (type is not null)
        {
            visits = visits.Where(v => v.Type == type);
        }

        if (status is not null)
        {
            visits = visits.Where(v => v.Status == status);
        }

        if (departmentId is not null)
        {
            visits = visits.Where(v => v.DepartmentId == departmentId);
        }

        if (practitionerId is not null)
        {
            visits = visits.Where(v => v.PractitionerId == practitionerId);
        }

        if (page.SearchTerm is not null)
        {
            var matching = db.Set<Patient>().Search(page.SearchTerm, protector).Select(p => p.Id);
            visits = visits.Where(v => matching.Contains(v.PatientId) || v.VisitNumber == page.SearchTerm.ToUpper());
        }

        var result = await (
                from v in visits
                join p in db.Set<Patient>() on v.PatientId equals p.Id
                join d in db.Set<Department>() on v.DepartmentId equals d.Id into departments
                from d in departments.DefaultIfEmpty()
                join pr in db.Set<Practitioner>() on v.PractitionerId equals pr.Id into practitioners
                from pr in practitioners.DefaultIfEmpty()
                join t in db.Set<QueueToken>() on v.Id equals t.VisitId into tokens
                from t in tokens.DefaultIfEmpty()
                orderby v.VisitedAt descending
                select new
                {
                    v.Id,
                    v.VisitNumber,
                    v.PatientId,
                    p.Uhid,
                    p.FullName,
                    p.IsVip,
                    p.Gender,
                    p.DateOfBirth,
                    v.Type,
                    v.Status,
                    v.DepartmentId,
                    DepartmentName = d != null ? d.Name : null,
                    v.PractitionerId,
                    PractitionerName = pr != null ? pr.DisplayName : null,
                    v.VisitedAt,
                    v.CompletedAt,
                    v.IsFollowUp,
                    v.IsFreeFollowUp,
                    v.PayerType,
                    v.IsMlc,
                    TokenLabel = t != null ? t.Label : null,
                    TokenStatus = t != null ? (TokenStatus?)t.Status : null,
                })
            .ToPagedResultAsync(page, ct);

        // A visit list shows names, UHIDs and ages: who listed or searched visits, and whom they were shown, is PHI access,
        // as a patient search is (A-1).
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "visit.searched",
            page.SearchTerm is null ? "Listed visits" : $"Searched visits for \"{page.SearchTerm}\"")
        {
            Details = new
            {
                term = page.SearchTerm,
                from = first,
                to = last,
                type,
                status,
                departmentId,
                practitionerId,
                page = page.Page,
                matched = result.TotalCount,
                shown = result.Items.Select(v => v.PatientId).Distinct(),
            },
        }, ct);

        // A restricted (VIP) patient's name shows only to those who may open the record (R-3).
        var opened = await access.OpenableAsync([.. result.Items.Where(v => v.IsVip).Select(v => v.PatientId).Distinct()], ct);
        return TypedResults.Ok(result.Map(v => new VisitListItem(
            v.Id, v.VisitNumber, v.PatientId, v.Uhid, v.IsVip && !opened.Contains(v.PatientId) ? PatientNames.Restricted(v.FullName) : v.FullName, v.Gender,
            v.DateOfBirth is { } dob ? Age.Format(dob, today) : null, v.Type, v.Status, v.DepartmentId, v.DepartmentName, v.PractitionerId, v.PractitionerName,
            v.VisitedAt, v.CompletedAt, v.IsFollowUp, v.IsFreeFollowUp, v.PayerType, v.IsMlc, v.TokenLabel, v.TokenStatus)));
    }

    /// <summary>
    /// A visit, opened as the patient's record is (<see cref="PatientAccessGuard.EnsureCanOpenAsync"/>) and recorded as access
    /// to it. When the patient's chart is closed to the user (a sensitive category they do not hold), the complaint, the
    /// notes and the MLC number are left out (<see cref="VisitPrivacy"/>).
    /// </summary>
    private static async Task<IResult> Get(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var visit = await db.Set<Visit>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
        if (visit is null)
        {
            return Error.NotFound(nameof(Visit), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(visit.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var token = await db.Set<QueueToken>().AsNoTracking().FirstOrDefaultAsync(t => t.VisitId == id, ct);
        var detail = VisitService.ToDetail(visit, token);
        var withheld = await guard.WithheldChartsAsync([visit.PatientId], ct);
        await audit.PatientRecordAccessedAsync(visit.PatientId, $"Viewed visit {visit.VisitNumber}", ct);
        return TypedResults.Ok(withheld.ContainsKey(visit.PatientId) ? VisitPrivacy.Withhold(detail) : detail);
    }

    private static async Task<IResult> Create(CreateVisitRequest request, VisitService visits, CancellationToken ct) =>
        (await visits.CreateAsync(request, ct)).ToCreated(v => $"/api/v1/visits/{v.Id}");

    /// <summary>
    /// 204 once cancelled; 202 with the approval request when the doctor has started the visit and a second person decides
    /// (<see cref="VisitService.AskCancelAsync"/>).
    /// </summary>
    private static async Task<IResult> Cancel(Guid id, CancelVisitRequest request, VisitService visits, CancellationToken ct) =>
        (await visits.AskCancelAsync(id, request.Reason, ct)).ToHttpResult(outcome => outcome.Asked is { } asked
            ? TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked)
            : TypedResults.NoContent());

    private static async Task<IResult> Complete(Guid id, VisitService visits, CancellationToken ct) =>
        (await visits.CompleteAsync(id, ct)).ToNoContent();

    /// <summary>The patient's visits, opened as the patient's record is and recorded as access to it.</summary>
    private static async Task<IResult> ForPatient(
        Guid patientId, [AsParameters] PageQuery query, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var page = query.ToRequest();
        var today = time.Today();
        var result = await (
                from v in db.Set<Visit>().AsNoTracking().Where(v => v.PatientId == patientId)
                join p in db.Set<Patient>() on v.PatientId equals p.Id
                join d in db.Set<Department>() on v.DepartmentId equals d.Id into departments
                from d in departments.DefaultIfEmpty()
                join pr in db.Set<Practitioner>() on v.PractitionerId equals pr.Id into practitioners
                from pr in practitioners.DefaultIfEmpty()
                orderby v.VisitedAt descending
                select new
                {
                    v.Id,
                    v.VisitNumber,
                    v.PatientId,
                    p.Uhid,
                    p.FullName,
                    p.Gender,
                    p.DateOfBirth,
                    v.Type,
                    v.Status,
                    v.DepartmentId,
                    DepartmentName = d != null ? d.Name : null,
                    v.PractitionerId,
                    PractitionerName = pr != null ? pr.DisplayName : null,
                    v.VisitedAt,
                    v.CompletedAt,
                    v.IsFollowUp,
                    v.IsFreeFollowUp,
                    v.PayerType,
                    v.IsMlc,
                })
            .ToPagedResultAsync(page, ct);

        await audit.PatientRecordAccessedAsync(patientId, "Viewed visit history", ct);
        return TypedResults.Ok(result.Map(v => new VisitListItem(
            v.Id, v.VisitNumber, v.PatientId, v.Uhid, v.FullName, v.Gender, v.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            v.Type, v.Status, v.DepartmentId, v.DepartmentName, v.PractitionerId, v.PractitionerName, v.VisitedAt, v.CompletedAt,
            v.IsFollowUp, v.IsFreeFollowUp, v.PayerType, v.IsMlc, null, null)));
    }

    /// <summary>
    /// The patient banner's payer (<see cref="PatientPayerRule"/>), with the name and policy from Billing: opened as the
    /// patient's record is, and recorded as access to it.
    /// </summary>
    private static async Task<IResult> PayerForPatient(
        Guid patientId, HospitalDbContext db, IPayerDirectory payers, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        // The two candidates the rule chooses between: the newest open visit, and the newest that was not cancelled.
        var visits = db.Set<Visit>().AsNoTracking().Where(v => v.PatientId == patientId).OrderByDescending(v => v.VisitedAt);
        var candidates = new List<PayerVisit>();
        foreach (var candidate in new[]
                 {
                     visits.Where(v => v.Status == VisitStatus.Open || v.Status == VisitStatus.InProgress),
                     visits.Where(v => v.Status != VisitStatus.Cancelled),
                 })
        {
            if (await candidate.Select(v => new { v.Id, v.Status, v.VisitedAt, v.PayerType, v.PayerId }).FirstOrDefaultAsync(ct) is { } v)
            {
                candidates.Add(new PayerVisit(v.Id, v.Status, v.VisitedAt, v.PayerType, v.PayerId));
            }
        }

        PatientPayer? current = null;
        if (PatientPayerRule.Choose(candidates) is { } chosen)
        {
            var (visit, isOpen) = chosen;
            var coverage = visit.PayerType != PayerType.Self && visit.PayerId is { } payerId
                ? await payers.GetCoverageAsync(patientId, payerId, ct)
                : new PayerCoverageSummary(null, null, null);
            current = new PatientPayer(visit.VisitId, isOpen, visit.PayerType, visit.PayerId, coverage.PayerName, coverage.PolicyNumber, coverage.ValidUntil);
        }

        await audit.PatientRecordAccessedAsync(patientId, "Viewed the patient's payer", ct);
        return TypedResults.Ok(new PatientPayerResponse(current));
    }

    private static DateTimeOffset ToUtcStart(DateOnly localDate)
    {
        var start = localDate.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(start, HospitalTimeZone.Zone.GetUtcOffset(start)).ToUniversalTime();
    }
}
