using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.FollowUps;

/// <summary>
/// /api/v1/crm/follow-ups: the call worklist (CRM-02). Lists show masked numbers. Opening a task shows the full number
/// to call; this respects restricted records and is audited as patient-record access.
/// </summary>
internal static class FollowUpEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/crm/follow-ups").WithTags("CRM").RequirePermission(EngagementPermissions.FollowUpsManage);

        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", async (CreateFollowUpRequest r, FollowUpService followUps, CancellationToken ct) =>
                (await followUps.CreateAsync(r, ct)).ToCreated(id => $"/api/v1/crm/follow-ups/{id}"))
            .WithValidation<CreateFollowUpRequest>();
        group.MapPut("/{id:guid}", async (Guid id, UpdateFollowUpRequest r, FollowUpService followUps, CancellationToken ct) =>
                (await followUps.UpdateAsync(id, r, ct)).ToNoContent())
            .WithValidation<UpdateFollowUpRequest>();
        group.MapPost("/{id:guid}/attempts", async (Guid id, FollowUpAttemptRequest r, FollowUpService followUps, CancellationToken ct) =>
                (await followUps.RecordAttemptAsync(id, r, ct)).ToNoContent())
            .WithValidation<FollowUpAttemptRequest>();
        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelFollowUpRequest r, FollowUpService followUps, CancellationToken ct) =>
                (await followUps.CancelAsync(id, r.Reason, ct)).ToNoContent())
            .WithValidation<CancelFollowUpRequest>();
        group.MapPost("/{id:guid}/remind", async (Guid id, FollowUpService followUps, CancellationToken ct) =>
            (await followUps.SendReminderAsync(id, ct)).ToNoContent());
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        FollowUpStatus? status,
        FollowUpType? type,
        FollowUpPriority? priority,
        Guid? assigneeUserId,
        bool? mine,
        bool? overdueOnly,
        bool? includeClosed,
        DateOnly? dueFrom,
        DateOnly? dueTo,
        HospitalDbContext db,
        ICurrentUser user,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.Today();
        var tasks = db.Set<FollowUpTask>().AsNoTracking();
        if (status is not null)
        {
            tasks = tasks.Where(t => t.Status == status);
        }
        else if (includeClosed != true)
        {
            tasks = tasks.Where(t => t.Status == FollowUpStatus.Open || t.Status == FollowUpStatus.InProgress);
        }

        if (type is not null)
        {
            tasks = tasks.Where(t => t.Type == type);
        }

        if (priority is not null)
        {
            tasks = tasks.Where(t => t.Priority == priority);
        }

        var assignee = mine == true ? user.UserId : assigneeUserId;
        if (assignee is not null)
        {
            tasks = tasks.Where(t => t.AssigneeUserId == assignee);
        }

        if (overdueOnly == true)
        {
            tasks = tasks.Where(t => (t.Status == FollowUpStatus.Open || t.Status == FollowUpStatus.InProgress) && t.DueDate < today);
        }

        if (dueFrom is not null)
        {
            tasks = tasks.Where(t => t.DueDate >= dueFrom);
        }

        if (dueTo is not null)
        {
            tasks = tasks.Where(t => t.DueDate <= dueTo);
        }

        var rows =
            from t in tasks
            join p in db.Set<Patient>() on t.PatientId equals p.Id
            select new { Task = t, p.Uhid, p.FullName, p.Phone };
        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            var uhid = term.ToUpperInvariant();
            rows = rows.Where(r => r.Uhid == uhid || EF.Functions.ILike(r.FullName, like, QueryExtensions.LikeEscape));
        }

        // Enums are stored as text, so priority is ranked explicitly: high-priority calls first, then by due date.
        var result = await rows
            .OrderByDescending(r => r.Task.Priority == FollowUpPriority.High).ThenBy(r => r.Task.DueDate).ThenBy(r => r.Task.CreatedAt)
            .ToPagedResultAsync(page, ct);

        return TypedResults.Ok(result.Map(r => new FollowUpListItem(
            r.Task.Id, r.Task.PatientId, r.Uhid, r.FullName, r.Phone is null ? null : Phone.Mask(r.Phone), r.Task.Type, r.Task.DueDate, r.Task.Status,
            r.Task.Priority, r.Task.AssigneeUserId, r.Task.Attempts, r.Task.Outcome, r.Task.IsOverdue(today), r.Task.ReminderSentAt)));
    }

    private static async Task<IResult> Get(
        Guid id,
        HospitalDbContext db,
        PatientAccessGuard guard,
        IAuditLogger audit,
        TimeProvider time,
        CancellationToken ct)
    {
        var task = await db.Set<FollowUpTask>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return Error.NotFound(nameof(FollowUpTask), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(task.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == task.PatientId)
            .Select(p => new { p.Uhid, p.FullName, p.Phone })
            .FirstAsync(ct);
        await audit.PatientRecordAccessedAsync(task.PatientId, "Opened a CRM follow-up call", ct);

        return TypedResults.Ok(new FollowUpDetail(
            task.Id, task.PatientId, patient.Uhid, patient.FullName, patient.Phone, task.VisitId, task.AdmissionId, task.Type, task.DueDate, task.Status,
            task.Priority, task.AssigneeUserId, task.Notes, task.Attempts, task.Outcome, task.OutcomeNotes, task.CompletedAt, task.Source,
            task.IsOverdue(time.Today()), task.ReminderSentAt, task.Version));
    }
}
