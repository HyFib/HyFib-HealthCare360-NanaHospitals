using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Quality.Features.Incidents;

/// <summary>/api/v1/quality/incidents — reporting (all staff) and review (quality team), IRMS-01..03.</summary>
internal static class IncidentEndpoints
{
    internal static readonly Dictionary<string, Expression<Func<Incident, object?>>> SortKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["occurredAt"] = i => i.OccurredAt,
        ["reportedAt"] = i => i.ReportedAt,
        // Severity is stored as text; rank it explicitly so SQL sorts by harm, not alphabetically.
        ["severity"] = i => i.Severity == IncidentSeverity.Death ? 5
            : i.Severity == IncidentSeverity.Severe ? 4
            : i.Severity == IncidentSeverity.Moderate ? 3
            : i.Severity == IncidentSeverity.Mild ? 2
            : i.Severity == IncidentSeverity.NoHarm ? 1
            : 0,
        ["number"] = i => i.IncidentNumber,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/incidents").WithTags("Quality");

        group.MapPost("/", async (ReportIncidentRequest r, IncidentService incidents, CancellationToken ct) =>
                (await incidents.ReportAsync(r, ct)).ToCreated(receipt => $"/api/v1/quality/incidents/{receipt.Id}"))
            .RequirePermission(QualityPermissions.IncidentsReport).WithValidation<ReportIncidentRequest>();
        group.MapGet("/mine", Mine).RequirePermission(QualityPermissions.IncidentsReport);

        group.MapGet("/", List).RequirePermission(QualityPermissions.IncidentsManage);
        group.MapGet("/{id:guid}", Get).RequirePermission(QualityPermissions.IncidentsManage);
        group.MapPut("/{id:guid}", async (Guid id, UpdateIncidentRequest r, IncidentService incidents, CancellationToken ct) =>
                (await incidents.UpdateAsync(id, r, ct)).ToNoContent())
            .RequirePermission(QualityPermissions.IncidentsManage).WithValidation<UpdateIncidentRequest>();
        group.MapPost("/{id:guid}/transition", async (Guid id, TransitionIncidentRequest r, IncidentService incidents, CancellationToken ct) =>
                (await incidents.TransitionAsync(id, r, ct)).ToNoContent())
            .RequirePermission(QualityPermissions.IncidentsManage).WithValidation<TransitionIncidentRequest>();
        group.MapPost("/{id:guid}/assign", async (Guid id, AssignIncidentRequest r, IncidentService incidents, CancellationToken ct) =>
                (await incidents.AssignAsync(id, r, ct)).ToNoContent())
            .RequirePermission(QualityPermissions.IncidentsManage);
    }

    /// <summary>The caller's own named reports (anonymous reports are, by design, not traceable to anyone).</summary>
    private static async Task<IResult> Mine(HospitalDbContext db, ICurrentUser user, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<Incident>().AsNoTracking()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(i => i.ReporterUserId == user.UserId && i.ReporterUserId != null)
            .OrderByDescending(i => i.ReportedAt)
            .Take(100)
            .Select(i => new MyIncidentItem(i.Id, i.IncidentNumber, i.Type, i.Severity, i.Status, i.ReportedAt))
            .ToListAsync(ct));

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        IncidentStatus? status,
        IncidentType? type,
        IncidentSeverity? severity,
        bool? sentinelOnly,
        bool? openOnly,
        Guid? departmentId,
        DateOnly? from,
        DateOnly? to,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var incidents = db.Set<Incident>().AsNoTracking();
        if (status is not null)
        {
            incidents = incidents.Where(i => i.Status == status);
        }
        else if (openOnly == true)
        {
            incidents = incidents.Where(i => i.Status != IncidentStatus.Closed);
        }

        if (type is not null)
        {
            incidents = incidents.Where(i => i.Type == type);
        }

        if (severity is not null)
        {
            incidents = incidents.Where(i => i.Severity == severity);
        }

        if (sentinelOnly == true)
        {
            incidents = incidents.Where(i => i.IsSentinel);
        }

        if (departmentId is not null)
        {
            incidents = incidents.Where(i => i.DepartmentId == departmentId);
        }

        if (from is { } start)
        {
            var since = QualityClock.StartOfDayUtc(start);
            incidents = incidents.Where(i => i.OccurredAt >= since);
        }

        if (to is { } end)
        {
            var until = QualityClock.StartOfDayUtc(end.AddDays(1));
            incidents = incidents.Where(i => i.OccurredAt < until);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            incidents = incidents.Where(i => i.IncidentNumber == term.ToUpper() || EF.Functions.ILike(i.Description, like) || EF.Functions.ILike(i.Location, like));
        }

        var result = await incidents.OrderByKey(page, SortKeys, i => i.ReportedAt, defaultDescending: true)
            .Select(i => new
            {
                i.Id, i.IncidentNumber, i.Type, i.Severity, i.IsSentinel, i.Status, i.OccurredAt, i.ReportedAt, i.Location, i.DepartmentId,
                DepartmentName = db.Set<Department>().Where(d => d.Id == i.DepartmentId).Select(d => d.Name).FirstOrDefault(),
                i.IsAnonymous, i.AssignedToUserId,
                OpenCapas = db.Set<Domain.Capa>().Count(c => c.IncidentId == i.Id && (c.Status == CapaStatus.Open || c.Status == CapaStatus.InProgress)),
            })
            .ToPagedResultAsync(page, ct);

        return TypedResults.Ok(result.Map(i => new IncidentListItem(
            i.Id, i.IncidentNumber, i.Type, i.Severity, i.IsSentinel, i.Status, i.OccurredAt, i.ReportedAt, i.Location, i.DepartmentId,
            i.DepartmentName, i.IsAnonymous, i.AssignedToUserId, i.OpenCapas)));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var incident = await db.Set<Incident>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null)
        {
            return Error.NotFound(nameof(Incident), id).ToProblem();
        }

        var userIds = incident.InvolvedStaffUserIds.Append(incident.AssignedToUserId ?? Guid.Empty).Distinct().ToList();
        var names = await db.Set<User>().AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var department = incident.DepartmentId is { } departmentId
            ? await db.Set<Department>().Where(d => d.Id == departmentId).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;
        var investigation = await db.Set<Investigation>().Where(i => i.IncidentId == id).Select(i => (InvestigationStatus?)i.Status).FirstOrDefaultAsync(ct);
        var today = time.LocalToday();
        var capas = (await db.Set<Domain.Capa>().AsNoTracking().Where(c => c.IncidentId == id).OrderBy(c => c.DueDate).ToListAsync(ct))
            .Select(c => new IncidentCapaItem(c.Id, c.CapaNumber, c.Type, c.Action, c.Status, c.DueDate, c.IsOverdue(today)))
            .ToList();

        if (incident.PatientId is { } patientId)
        {
            await audit.PatientRecordAccessedAsync(patientId, $"Viewed incident report {incident.IncidentNumber}", ct);
        }

        return TypedResults.Ok(new IncidentDetail(
            incident.Id, incident.IncidentNumber, incident.Type, incident.Severity, incident.IsSentinel, incident.SentinelKind, incident.SentinelReason,
            incident.Status, IncidentWorkflow.NextStates(incident.Status), incident.OccurredAt, incident.ReportedAt, incident.Location, incident.DepartmentId,
            department, incident.PatientId,
            incident.InvolvedStaffUserIds.Select(u => new PersonRef(u, names.GetValueOrDefault(u) ?? "Unknown user")).ToList(),
            incident.Description, incident.ImmediateAction, incident.Witnesses, incident.IsAnonymous, incident.ReporterName,
            incident.AssignedToUserId is { } assignee ? new PersonRef(assignee, names.GetValueOrDefault(assignee) ?? "Unknown user") : null,
            incident.ClosedAt, incident.ClosureNote, incident.LastStatusNote, incident.ReopenCount, investigation, capas, incident.Version));
    }
}
