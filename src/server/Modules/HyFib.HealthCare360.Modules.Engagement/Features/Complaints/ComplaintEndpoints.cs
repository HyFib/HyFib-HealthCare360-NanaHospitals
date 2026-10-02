using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Complaints;

/// <summary>/api/v1/feedback/complaints: complaint handling with SLA and turnaround time (FB-03).</summary>
internal static class ComplaintEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/feedback/complaints").WithTags("Feedback").RequirePermission(EngagementPermissions.ComplaintsManage);

        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", Create).WithValidation<CreateComplaintRequest>();
        group.MapPut("/{id:guid}", async (Guid id, UpdateComplaintRequest r, ComplaintService complaints, CancellationToken ct) =>
                (await complaints.UpdateAsync(id, r, ct)).ToNoContent())
            .WithValidation<UpdateComplaintRequest>();
        group.MapPost("/{id:guid}/status", async (Guid id, ChangeComplaintStatusRequest r, ComplaintService complaints, CancellationToken ct) =>
                (await complaints.ChangeStatusAsync(id, r, ct)).ToNoContent())
            .WithValidation<ChangeComplaintStatusRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        ComplaintStatus? status,
        ComplaintPriority? priority,
        ComplaintSource? source,
        Guid? departmentId,
        Guid? ownerUserId,
        bool? mine,
        bool? openOnly,
        bool? overdueOnly,
        DateOnly? from,
        DateOnly? to,
        HospitalDbContext db,
        ICurrentUser user,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var now = time.GetUtcNow();
        var complaints = db.Set<Complaint>().AsNoTracking();
        if (status is not null)
        {
            complaints = complaints.Where(c => c.Status == status);
        }
        else if (openOnly == true || overdueOnly == true)
        {
            complaints = complaints.Where(c => c.Status == ComplaintStatus.Open || c.Status == ComplaintStatus.InProgress);
        }

        if (overdueOnly == true)
        {
            complaints = complaints.Where(c => c.DueAt < now);
        }

        if (priority is not null)
        {
            complaints = complaints.Where(c => c.Priority == priority);
        }

        if (source is not null)
        {
            complaints = complaints.Where(c => c.Source == source);
        }

        if (departmentId is not null)
        {
            complaints = complaints.Where(c => c.DepartmentId == departmentId);
        }

        var owner = mine == true ? user.UserId : ownerUserId;
        if (owner is not null)
        {
            complaints = complaints.Where(c => c.OwnerUserId == owner);
        }

        if (from is { } start)
        {
            var since = EngagementClock.StartOfDayUtc(start);
            complaints = complaints.Where(c => c.RaisedAt >= since);
        }

        if (to is { } end)
        {
            var until = EngagementClock.StartOfDayUtc(end.AddDays(1));
            complaints = complaints.Where(c => c.RaisedAt < until);
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            complaints = complaints.Where(c => c.ComplaintNumber == number || c.ComplaintNumber.EndsWith(number));
        }

        // Open first, then high priority (enums are stored as text, so ranked explicitly), then the nearest deadline.
        var result = await complaints
            .OrderBy(c => c.Status == ComplaintStatus.Resolved || c.Status == ComplaintStatus.Closed)
            .ThenByDescending(c => c.Priority == ComplaintPriority.High)
            .ThenBy(c => c.DueAt)
            .ToPagedResultAsync(page, ct);

        return TypedResults.Ok(result.Map(c => new ComplaintListItem(
            c.Id, c.ComplaintNumber, c.Source, c.Status, c.Priority, c.Category, c.DepartmentId, c.PatientId, c.OwnerUserId, c.RaisedAt, c.DueAt,
            c.IsOverdue(now), c.TurnaroundTime is { } tat ? Math.Round(tat.TotalHours, 1) : null, c.EscalatedAt)));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var c = await db.Set<Complaint>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound(nameof(Complaint), id).ToProblem();
        }

        if (c.PatientId is { } patientId)
        {
            await audit.PatientRecordAccessedAsync(patientId, $"Viewed complaint {c.ComplaintNumber}", ct);
        }

        return TypedResults.Ok(new ComplaintDetail(
            c.Id, c.ComplaintNumber, c.Source, c.Status, c.Priority, c.Category, c.Description, c.DepartmentId, c.PatientId, c.FeedbackResponseId,
            c.ContactName, c.ContactPhone, c.OwnerUserId, c.Resolution, c.RaisedAt, c.DueAt, c.ResolvedAt, c.ClosedAt, c.ReopenCount,
            c.IsOverdue(time.GetUtcNow()), c.EscalatedAt, c.Version));
    }

    private static async Task<IResult> Create(CreateComplaintRequest request, ComplaintService complaints, ICurrentUser user, CancellationToken ct) =>
        user.FacilityId is { } facilityId
            ? (await complaints.CreateAsync(facilityId, request, ct)).ToCreated(id => $"/api/v1/feedback/complaints/{id}")
            : Error.Rule("facility.required", "Select a facility first.").ToProblem();
}
