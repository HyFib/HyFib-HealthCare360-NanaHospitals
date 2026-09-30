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
    }

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
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.Today();
        var start = ToUtcStart(from ?? today);
        var end = ToUtcStart((to ?? from ?? today).AddDays(1));

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

        return TypedResults.Ok(result.Map(v => new VisitListItem(
            v.Id, v.VisitNumber, v.PatientId, v.Uhid, v.FullName, v.Gender, v.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            v.Type, v.Status, v.DepartmentId, v.DepartmentName, v.PractitionerId, v.PractitionerName, v.VisitedAt, v.CompletedAt,
            v.IsFollowUp, v.IsFreeFollowUp, v.PayerType, v.IsMlc, v.TokenLabel, v.TokenStatus)));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var visit = await db.Set<Visit>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
        if (visit is null)
        {
            return Error.NotFound(nameof(Visit), id).ToProblem();
        }

        var token = await db.Set<QueueToken>().AsNoTracking().FirstOrDefaultAsync(t => t.VisitId == id, ct);
        return TypedResults.Ok(VisitService.ToDetail(visit, token));
    }

    private static async Task<IResult> Create(CreateVisitRequest request, VisitService visits, CancellationToken ct) =>
        (await visits.CreateAsync(request, ct)).ToCreated(v => $"/api/v1/visits/{v.Id}");

    private static async Task<IResult> Cancel(Guid id, CancelVisitRequest request, VisitService visits, CancellationToken ct) =>
        (await visits.CancelAsync(id, request.Reason, ct)).ToNoContent();

    private static async Task<IResult> Complete(Guid id, VisitService visits, CancellationToken ct) =>
        (await visits.CompleteAsync(id, ct)).ToNoContent();

    private static async Task<IResult> ForPatient(Guid patientId, [AsParameters] PageQuery query, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
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

        return TypedResults.Ok(result.Map(v => new VisitListItem(
            v.Id, v.VisitNumber, v.PatientId, v.Uhid, v.FullName, v.Gender, v.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            v.Type, v.Status, v.DepartmentId, v.DepartmentName, v.PractitionerId, v.PractitionerName, v.VisitedAt, v.CompletedAt,
            v.IsFollowUp, v.IsFreeFollowUp, v.PayerType, v.IsMlc, null, null)));
    }

    private static DateTimeOffset ToUtcStart(DateOnly localDate)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            var start = localDate.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(start, zone.GetUtcOffset(start)).ToUniversalTime();
        }
        catch (TimeZoneNotFoundException)
        {
            return new DateTimeOffset(localDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }
    }
}
