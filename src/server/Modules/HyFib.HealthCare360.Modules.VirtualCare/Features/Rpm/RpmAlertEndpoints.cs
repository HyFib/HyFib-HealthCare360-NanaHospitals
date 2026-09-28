using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Services;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;

/// <summary>/api/v1/rpm/alerts — the care team's alert queue with acknowledgement tracking (RPM-03/05).</summary>
internal static class RpmAlertEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rpm/alerts").WithTags("Remote Monitoring");

        group.MapGet("/", List).RequirePermission(VirtualCarePermissions.RpmRead);
        group.MapPost("/{id:guid}/acknowledge", (Guid id, AlertActionRequest request, HospitalDbContext db, ICurrentUser user, IRealtimePublisher realtime, TimeProvider time, CancellationToken ct) =>
                ChangeAsync(id, (alert, now) => alert.Acknowledge(user.UserId, request.Notes, now), db, realtime, time, ct))
            .RequirePermission(VirtualCarePermissions.RpmAlerts)
            .WithValidation<AlertActionRequest>();
        group.MapPost("/{id:guid}/resolve", (Guid id, AlertActionRequest request, HospitalDbContext db, ICurrentUser user, IRealtimePublisher realtime, TimeProvider time, CancellationToken ct) =>
                ChangeAsync(id, (alert, now) => alert.Resolve(user.UserId, request.Notes, now), db, realtime, time, ct))
            .RequirePermission(VirtualCarePermissions.RpmAlerts)
            .WithValidation<AlertActionRequest>();
    }

    /// <summary>Active alerts by default, critical first, newest first.</summary>
    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        RpmAlertStatus? status,
        RpmAlertSeverity? severity,
        Guid? enrollmentId,
        Guid? patientId,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var alerts = db.Set<RpmAlert>().AsNoTracking();
        alerts = status is { } wanted
            ? alerts.Where(a => a.Status == wanted)
            : alerts.Where(a => a.Status == RpmAlertStatus.Open || a.Status == RpmAlertStatus.Acknowledged);

        if (severity is not null)
        {
            alerts = alerts.Where(a => a.Severity == severity);
        }

        if (enrollmentId is not null)
        {
            alerts = alerts.Where(a => a.EnrollmentId == enrollmentId);
        }

        if (patientId is not null)
        {
            alerts = alerts.Where(a => a.PatientId == patientId);
        }

        var result = await (
                from a in alerts
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                orderby a.Severity == RpmAlertSeverity.Critical descending, a.Status == RpmAlertStatus.Open descending, a.RaisedAt descending
                select new RpmAlertDto(
                    a.Id, a.EnrollmentId, a.PatientId, p.Uhid, p.FullName, a.Kind, a.Severity, a.Status, a.ReadingType, a.Value, a.Unit, a.Message,
                    a.RaisedAt, a.LastOccurredAt, a.Occurrences, a.AcknowledgedAt, a.ResolvedAt, a.Notes))
            .ToPagedResultAsync(query.ToRequest(), ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> ChangeAsync(
        Guid id, Func<RpmAlert, DateTimeOffset, Result> change, HospitalDbContext db, IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var alert = await db.Set<RpmAlert>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null)
        {
            return Error.NotFound(nameof(RpmAlert), id).ToProblem();
        }

        var result = change(alert, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        await realtime.ToFacilityAsync(alert.FacilityId, RpmAlertService.ChangedEvent, new { alert.Id, alert.Status }, ct);
        return TypedResults.NoContent();
    }
}
