using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Services;

namespace HyFib.HealthCare360.Modules.Analytics.Features.Schedules;

/// <summary>/api/v1/analytics/schedules — scheduled report e-mails and their deliveries (MIS-05, BI-04).</summary>
internal static class ScheduleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/analytics").WithTags("Analytics").RequirePermission(AnalyticsPermissions.SchedulesManage);

        group.MapGet("/schedules", List);
        group.MapGet("/schedules/{id:guid}", Get);
        group.MapPost("/schedules", Create).WithValidation<SaveScheduleRequest>();
        group.MapPut("/schedules/{id:guid}", Update).WithValidation<SaveScheduleRequest>();
        group.MapDelete("/schedules/{id:guid}", Delete);
        group.MapPost("/schedules/{id:guid}/send-now", SendNow);
        group.MapGet("/schedules/{id:guid}/deliveries", Deliveries);
        group.MapGet("/deliveries/{id:guid}/file", DownloadFile);
    }

    private static async Task<IResult> List(ReportActors actors, ScheduleService schedules, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? TypedResults.Ok(await schedules.ListAsync(actor, ct)) : SignInRequired();

    private static async Task<IResult> Get(Guid id, ReportActors actors, ScheduleService schedules, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? (await schedules.GetAsync(actor, id, ct)).ToOk() : SignInRequired();

    private static async Task<IResult> Create(SaveScheduleRequest request, ReportActors actors, ScheduleService schedules, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor
            ? (await schedules.CreateAsync(actor, request, ct)).ToCreated(s => $"/api/v1/analytics/schedules/{s.Id}")
            : SignInRequired();

    private static async Task<IResult> Update(Guid id, SaveScheduleRequest request, ReportActors actors, ScheduleService schedules, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? (await schedules.UpdateAsync(actor, id, request, ct)).ToOk() : SignInRequired();

    private static async Task<IResult> Delete(Guid id, uint? version, ScheduleService schedules, CancellationToken ct) =>
        (await schedules.DeleteAsync(id, version, ct)).ToNoContent();

    /// <summary>Generates the file now and queues the e-mails (the regular schedule is unchanged).</summary>
    private static async Task<IResult> SendNow(Guid id, ReportActors actors, HospitalDbContext db, ReportDeliveryService deliveries, CancellationToken ct)
    {
        if (await actors.CurrentAsync(ct) is not { } actor)
        {
            return SignInRequired();
        }

        var schedule = await db.Set<ReportSchedule>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return Error.NotFound("Schedule", id).ToProblem();
        }

        var delivery = await deliveries.DeliverAsync(schedule, DeliveryTrigger.Manual, actor.UserId, ct);
        return delivery.Status == DeliveryStatus.Ready
            ? TypedResults.Ok(ReportDeliveryService.ToDto(delivery))
            : Error.Rule("analytics.delivery_failed", delivery.Error ?? "The report could not be generated.").ToProblem();
    }

    private static async Task<IResult> Deliveries(Guid id, [AsParameters] PageQuery query, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var deliveries = db.Set<ReportDelivery>().AsNoTracking().Where(d => d.ScheduleId == id);
        var total = await deliveries.LongCountAsync(ct);
        var rows = await deliveries
            .OrderByDescending(d => d.CreatedAt)
            .Skip(page.Skip)
            .Take(page.Take)
            .Select(d => new
            {
                d.Id,
                d.ScheduleId,
                d.ReportTitle,
                d.Trigger,
                d.CreatedAt,
                d.Status,
                d.PeriodFrom,
                d.PeriodTo,
                d.Format,
                d.FileName,
                d.SizeBytes,
                d.RowCount,
                HasFile = d.Content != null,
                d.Error,
            })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var recipients = (await db.Set<ReportDeliveryRecipient>().AsNoTracking().Where(r => ids.Contains(r.DeliveryId)).ToListAsync(ct))
            .ToLookup(r => r.DeliveryId);

        var items = rows.Select(d =>
            {
                var list = recipients[d.Id].OrderBy(r => r.Email, StringComparer.Ordinal).ToList();
                return new DeliveryDto(
                    d.Id, d.ScheduleId, d.ReportTitle, d.Trigger, d.CreatedAt, d.Status, d.PeriodFrom, d.PeriodTo, d.Format, d.FileName, d.SizeBytes, d.RowCount,
                    d.HasFile, d.Error, list.Count(r => r.Status == RecipientStatus.Sent), list.Count(r => r.Status == RecipientStatus.Pending),
                    list.Select(r => new DeliveryRecipientDto(r.Email, r.Status, r.Attempts, r.LastError, r.SentAt)).ToList());
            })
            .ToList();
        return TypedResults.Ok(new PagedResult<DeliveryDto>(items, page.Page, page.Take, total));
    }

    /// <summary>Downloads a delivered file again (the "link" alternative to the attachment); audited as an export.</summary>
    private static async Task<IResult> DownloadFile(Guid id, HttpContext http, HospitalDbContext db, IFieldProtector protector, IAuditLogger audit, CancellationToken ct)
    {
        var delivery = await db.Set<ReportDelivery>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (delivery is null)
        {
            return Error.NotFound("Delivery", id).ToProblem();
        }

        if (delivery.Content is null)
        {
            return Error.Rule("analytics.file_purged", "This file is no longer kept. Use \"Send now\" or open the report to create a new one.").ToProblem();
        }

        var content = protector.UnprotectBytes(delivery.Content, ReportDeliveryService.FilePurpose);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Data, "analytics.delivery.downloaded", $"Downloaded the scheduled report file '{delivery.FileName}'")
        {
            EntityType = nameof(ReportDelivery),
            EntityId = delivery.Id.ToString(),
            Details = new { delivery.ReportId, delivery.ScheduleId, delivery.PeriodFrom, delivery.PeriodTo, delivery.RowCount },
        }, ct);

        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.File(content, delivery.ContentType ?? "application/octet-stream", delivery.FileName ?? "report");
    }

    private static IResult SignInRequired() => Error.Forbidden("auth.required", "Sign in as a staff user to use analytics.").ToProblem();
}
