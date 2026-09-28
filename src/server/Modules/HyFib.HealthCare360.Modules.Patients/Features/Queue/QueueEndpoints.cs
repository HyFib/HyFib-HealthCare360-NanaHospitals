using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Features.Queue;

public sealed record QueueEntry(
    Guid TokenId,
    string Label,
    TokenStatus Status,
    TokenPriority Priority,
    Guid VisitId,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    Guid? PractitionerId,
    string? Room,
    DateTimeOffset IssuedAt,
    DateTimeOffset? CalledAt,
    int WaitingMinutes,
    string? ChiefComplaint,
    bool IsFollowUp);

public sealed record CallTokenRequest(string? Room);

public sealed record CallNextRequest(Guid DepartmentId, Guid? PractitionerId, string? Room);

public sealed record DisplayToken(string Label, string MaskedName, string? Room, DateTimeOffset? CalledAt);

public sealed record DisplayDepartment(Guid DepartmentId, string DepartmentName, IReadOnlyList<DisplayToken> NowServing, IReadOnlyList<string> UpNext, int Waiting);

/// <summary>/api/v1/queue — doctor/front-desk queue actions and the waiting-area display board.</summary>
internal static class QueueEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/queue").WithTags("Queue");

        group.MapGet("/", List).RequirePermission(PatientsPermissions.VisitsRead);
        group.MapPost("/call-next", async (CallNextRequest r, VisitService visits, CancellationToken ct) =>
                (await visits.CallNextAsync(r.DepartmentId, r.PractitionerId, r.Room, ct)).ToOk())
            .RequirePermission(PatientsPermissions.QueueManage);
        group.MapPost("/{id:guid}/call", async (Guid id, CallTokenRequest r, VisitService visits, CancellationToken ct) =>
                (await visits.CallAsync(id, r.Room, ct)).ToOk())
            .RequirePermission(PatientsPermissions.QueueManage);
        group.MapPost("/{id:guid}/start", async (Guid id, VisitService visits, CancellationToken ct) => (await visits.StartAsync(id, ct)).ToOk())
            .RequirePermission(PatientsPermissions.QueueManage);
        group.MapPost("/{id:guid}/complete", async (Guid id, VisitService visits, CancellationToken ct) => (await visits.FinishAsync(id, ct)).ToOk())
            .RequirePermission(PatientsPermissions.QueueManage);
        group.MapPost("/{id:guid}/skip", async (Guid id, VisitService visits, CancellationToken ct) => (await visits.SkipAsync(id, ct)).ToOk())
            .RequirePermission(PatientsPermissions.QueueManage);
        group.MapPost("/{id:guid}/requeue", async (Guid id, VisitService visits, CancellationToken ct) => (await visits.RequeueAsync(id, ct)).ToOk())
            .RequirePermission(PatientsPermissions.QueueManage);
        group.MapGet("/display", Display).RequirePermission(PatientsPermissions.QueueDisplay);
    }

    private static async Task<IResult> List(Guid departmentId, Guid? practitionerId, bool? includeClosed, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = time.Today();
        var now = time.GetUtcNow();
        var tokens = db.Set<QueueToken>().AsNoTracking().Where(t => t.DepartmentId == departmentId && t.TokenDate == today);
        if (practitionerId is not null)
        {
            tokens = tokens.Where(t => t.PractitionerId == null || t.PractitionerId == practitionerId);
        }

        if (includeClosed != true)
        {
            tokens = tokens.Where(t => t.Status != TokenStatus.Completed && t.Status != TokenStatus.Cancelled);
        }

        var rows = await (
                from t in tokens
                join p in db.Set<Patient>() on t.PatientId equals p.Id
                join v in db.Set<Visit>() on t.VisitId equals v.Id
                // Explicit ranks: enums are stored as text, so "Priority descending" in SQL would sort alphabetically.
                orderby t.Status == TokenStatus.InService descending,
                    t.Status == TokenStatus.Called descending,
                    t.Priority == TokenPriority.Emergency descending,
                    t.Priority == TokenPriority.Priority descending,
                    t.Sequence
                select new { t, p.Uhid, p.FullName, p.Gender, p.DateOfBirth, v.ChiefComplaint, v.IsFollowUp })
            .ToListAsync(ct);

        return TypedResults.Ok(rows.Select(r => new QueueEntry(
            r.t.Id, r.t.Label, r.t.Status, r.t.Priority, r.t.VisitId, r.t.PatientId, r.Uhid, r.FullName, r.Gender,
            r.DateOfBirth is { } dob ? Age.Format(dob, today) : null, r.t.PractitionerId, r.t.Room, r.t.IssuedAt, r.t.CalledAt,
            (int)Math.Max(0, ((r.t.CalledAt ?? now) - r.t.IssuedAt).TotalMinutes), r.ChiefComplaint, r.IsFollowUp)).ToList());
    }

    /// <summary>Waiting-area board: now serving (with room) and up next. Names are masked for privacy.</summary>
    private static async Task<IResult> Display(string? departmentIds, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = time.Today();
        var ids = (departmentIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        var departments = await db.Set<Department>().AsNoTracking()
            .Where(d => d.IsActive && d.Kind == DepartmentKind.Clinical && (ids.Count == 0 || ids.Contains(d.Id)))
            .OrderBy(d => d.DisplayOrder)
            .Select(d => new { d.Id, d.Name })
            .ToListAsync(ct);
        var departmentIdList = departments.Select(d => d.Id).ToList();

        var tokens = await (
                from t in db.Set<QueueToken>().AsNoTracking()
                join p in db.Set<Patient>() on t.PatientId equals p.Id
                where t.TokenDate == today && departmentIdList.Contains(t.DepartmentId)
                      && (t.Status == TokenStatus.Waiting || t.Status == TokenStatus.Called || t.Status == TokenStatus.InService)
                select new { t.DepartmentId, t.Label, t.Status, t.Priority, t.Sequence, t.Room, t.CalledAt, p.FirstName, p.LastName })
            .ToListAsync(ct);

        var boards = departments.Select(d =>
        {
            var mine = tokens.Where(t => t.DepartmentId == d.Id).ToList();
            var serving = mine.Where(t => t.Status is TokenStatus.Called or TokenStatus.InService)
                .OrderByDescending(t => t.CalledAt)
                .Take(4)
                .Select(t => new DisplayToken(t.Label, MaskName(t.FirstName, t.LastName), t.Room, t.CalledAt))
                .ToList();
            var waiting = mine.Where(t => t.Status == TokenStatus.Waiting).OrderByDescending(t => t.Priority).ThenBy(t => t.Sequence).ToList();
            return new DisplayDepartment(d.Id, d.Name, serving, waiting.Take(6).Select(t => t.Label).ToList(), waiting.Count);
        }).Where(b => b.NowServing.Count > 0 || b.Waiting > 0 || ids.Count > 0).ToList();

        return TypedResults.Ok(boards);
    }

    private static string MaskName(string firstName, string? lastName) =>
        string.IsNullOrWhiteSpace(lastName) ? firstName : $"{firstName} {lastName.Trim()[0]}.";
}
