using HyFib.HealthCare360.Modules.Quality.Domain.Audits;
using HyFib.HealthCare360.Modules.Quality.Services;

namespace HyFib.HealthCare360.Modules.Quality.Features.Audits;

/// <summary>
/// /api/v1/quality/audits — NABH internal audits: checklists by chapter, the schedule, findings, a CAPA for each
/// non-compliance and closure once the CAPAs are verified. The quality team schedules and closes; the lead auditor (any
/// member of staff) records the findings of their audits.
/// </summary>
internal static class AuditEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/audits").WithTags("Quality");

        group.MapGet("/templates", (NabhChapter? chapter, bool? includeInactive, AuditService audits, CancellationToken ct) =>
                audits.TemplatesAsync(chapter, includeInactive == true, ct))
            .RequirePermission(QualityPermissions.AuditsManage);
        group.MapPost("/templates", async (SaveAuditTemplateRequest r, AuditService audits, CancellationToken ct) =>
                (await audits.SaveTemplateAsync(null, r, ct)).ToCreated(t => $"/api/v1/quality/audits/templates/{t.Id}"))
            .RequirePermission(QualityPermissions.AuditsManage).WithValidation<SaveAuditTemplateRequest>();
        group.MapPut("/templates/{id:guid}", async (Guid id, SaveAuditTemplateRequest r, AuditService audits, CancellationToken ct) =>
                (await audits.SaveTemplateAsync(id, r, ct)).ToOk())
            .RequirePermission(QualityPermissions.AuditsManage).WithValidation<SaveAuditTemplateRequest>().RequireVersion();

        group.MapGet("/", (AuditStatus? status, NabhChapter? chapter, AuditService audits, CancellationToken ct) => audits.ListAsync(status, chapter, false, ct))
            .RequirePermission(QualityPermissions.AuditsManage);
        group.MapGet("/mine", (AuditService audits, CancellationToken ct) => audits.ListAsync(null, null, true, ct))
            .RequirePermission(QualityPermissions.IncidentsReport);
        group.MapGet("/{id:guid}", async (Guid id, AuditService audits, CancellationToken ct) => (await audits.GetAsync(id, ct)).ToOk())
            .RequireAnyPermission(QualityPermissions.AuditsManage, QualityPermissions.IncidentsReport);
        group.MapPost("/", async (ScheduleAuditRequest r, AuditService audits, CancellationToken ct) =>
                (await audits.ScheduleAsync(r, ct)).ToCreated(a => $"/api/v1/quality/audits/{a.Id}"))
            .RequirePermission(QualityPermissions.AuditsManage).WithValidation<ScheduleAuditRequest>();

        // The lead auditor records their own audit with the reporting permission every member of staff has; the service checks who.
        group.MapPost("/{id:guid}/start", async (Guid id, AuditStepRequest r, AuditService audits, CancellationToken ct) => (await audits.StartAsync(id, r, ct)).ToOk())
            .RequireAnyPermission(QualityPermissions.AuditsManage, QualityPermissions.IncidentsReport).WithValidation<AuditStepRequest>();
        group.MapPost("/{id:guid}/findings", async (Guid id, RecordFindingRequest r, AuditService audits, CancellationToken ct) => (await audits.RecordAsync(id, r, ct)).ToOk())
            .RequireAnyPermission(QualityPermissions.AuditsManage, QualityPermissions.IncidentsReport).WithValidation<RecordFindingRequest>();
        group.MapPost("/{id:guid}/complete", async (Guid id, CompleteAuditRequest r, AuditService audits, CancellationToken ct) =>
                (await audits.CompleteAsync(id, r, ct)).ToOk())
            .RequireAnyPermission(QualityPermissions.AuditsManage, QualityPermissions.IncidentsReport).WithValidation<CompleteAuditRequest>();

        // CAPAs are planned by the quality team, as every CAPA is.
        group.MapPost("/{id:guid}/findings/{findingId:guid}/capa", async (Guid id, Guid findingId, RaiseFindingCapaRequest r, AuditService audits, CancellationToken ct) =>
                (await audits.RaiseCapaAsync(id, findingId, r, ct)).ToOk())
            .RequirePermission(QualityPermissions.CapaManage).WithValidation<RaiseFindingCapaRequest>();
        group.MapPost("/{id:guid}/close", async (Guid id, AuditStepRequest r, AuditService audits, CancellationToken ct) => (await audits.CloseAsync(id, r, ct)).ToOk())
            .RequirePermission(QualityPermissions.AuditsManage).WithValidation<AuditStepRequest>();
        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelAuditRequest r, AuditService audits, CancellationToken ct) => (await audits.CancelAsync(id, r, ct)).ToOk())
            .RequirePermission(QualityPermissions.AuditsManage).WithValidation<CancelAuditRequest>();
    }
}
