using System.Text.Json;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Auditing;

public sealed record AuditEventDto(
    long Id,
    DateTimeOffset OccurredAt,
    string Category,
    string Action,
    AuditSeverity Severity,
    string? EntityType,
    string? EntityId,
    Guid? PatientId,
    Guid? UserId,
    string? UserName,
    string? IpAddress,
    string? Summary,
    string? Details,
    bool Sealed);

internal static class AuditEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/audit").WithTags("Administration").RequirePermission(PlatformPermissions.AuditRead);

        group.MapGet("/", async (
            [AsParameters] PageQuery query,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? category,
            string? action,
            Guid? userId,
            Guid? patientId,
            string? entityType,
            string? entityId,
            AuditSeverity? minSeverity,
            HospitalDbContext db,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var page = query.ToRequest();
            var events = db.Set<AuditEvent>().AsNoTracking();
            if (from is not null)
            {
                events = events.Where(e => e.OccurredAt >= from);
            }

            if (to is not null)
            {
                events = events.Where(e => e.OccurredAt <= to);
            }

            if (!string.IsNullOrWhiteSpace(category))
            {
                events = events.Where(e => e.Category == category);
            }

            if (!string.IsNullOrWhiteSpace(action))
            {
                events = events.Where(e => EF.Functions.ILike(e.Action, action.Trim().ToLikePattern(), QueryExtensions.LikeEscape));
            }

            if (userId is not null)
            {
                events = events.Where(e => e.UserId == userId);
            }

            if (patientId is not null)
            {
                events = events.Where(e => e.PatientId == patientId);
            }

            if (!string.IsNullOrWhiteSpace(entityType))
            {
                events = events.Where(e => e.EntityType == entityType);
            }

            if (!string.IsNullOrWhiteSpace(entityId))
            {
                events = events.Where(e => e.EntityId == entityId);
            }

            if (minSeverity is not null)
            {
                events = events.Where(e => e.Severity >= minSeverity);
            }

            if (page.SearchTerm is { } term)
            {
                var like = term.ToLikePattern();
                events = events.Where(e => (e.Summary != null && EF.Functions.ILike(e.Summary, like, QueryExtensions.LikeEscape)) || (e.UserName != null && EF.Functions.ILike(e.UserName, like, QueryExtensions.LikeEscape)));
            }

            // Reading the audit trail of a patient is itself PHI access.
            if (patientId is { } pid)
            {
                await audit.PatientRecordAccessedAsync(pid, "Viewed patient access log", ct);
            }

            var result = await events
                .OrderByDescending(e => e.Id)
                .Select(e => new AuditEventDto(e.Id, e.OccurredAt, e.Category, e.Action, e.Severity, e.EntityType, e.EntityId, e.PatientId, e.UserId,
                    e.UserName, e.IpAddress, e.Summary, e.Details, e.Hash != null))
                .ToPagedResultAsync(page, ct);
            return TypedResults.Ok(result);
        });

        group.MapGet("/export", Export);

        group.MapGet("/verify", async (AuditChainService chain, IAuditLogger audit, CancellationToken ct) =>
            TypedResults.Ok(await AuditChainChecks.VerifyAndRecordAsync(chain, audit, ct)));
    }

    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>The most an export holds; a longer period is exported in parts.</summary>
    internal const int MaxExportEvents = 200_000;

    /// <summary>
    /// The sealed audit trail from <paramref name="from"/> to <paramref name="to"/> for an auditor (<see cref="AuditExport"/>):
    /// the contiguous chain positions from the first event sealed in the period to the last, so it can be checked on its own,
    /// with the anchors that fall in it. Events not yet sealed (the last few seconds) are not in it. It holds patients' data,
    /// so exporting it is audited.
    /// </summary>
    private static async Task<IResult> Export(DateTimeOffset from, DateTimeOffset to, HospitalDbContext db, AuditAnchors anchors, IAuditLogger audit,
        ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        if (to < from)
        {
            return Error.Field("to", "The end of the period comes after its start.").ToProblem();
        }

        var sealedInPeriod = db.Set<AuditEvent>().AsNoTracking().Where(e => e.ChainIndex != null && e.OccurredAt >= from && e.OccurredAt <= to);
        var first = await sealedInPeriod.MinAsync(e => e.ChainIndex, ct);
        var last = await sealedInPeriod.MaxAsync(e => e.ChainIndex, ct);
        if (first is not { } fromIndex || last is not { } toIndex)
        {
            return Error.Rule("audit.export_empty", "No sealed audit records fall in this period.").ToProblem();
        }

        if (toIndex - fromIndex + 1 > MaxExportEvents)
        {
            return Error.Rule("audit.export_too_large",
                $"This period holds {toIndex - fromIndex + 1:N0} audit records; export at most {MaxExportEvents:N0} at a time, in shorter periods.").ToProblem();
        }

        var events = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.ChainIndex >= fromIndex && e.ChainIndex <= toIndex)
            .OrderBy(e => e.ChainIndex)
            .ToListAsync(ct);
        var inRange = anchors.Read().Anchors.Where(a => a.ChainIndex >= fromIndex && a.ChainIndex <= toIndex).ToList();
        await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "audit.exported",
            $"Exported audit records {fromIndex:N0} to {toIndex:N0} ({from:u} to {to:u})")
        {
            Severity = AuditSeverity.Warning,
            Details = new { from, to, fromIndex, toIndex, events = events.Count, anchors = inRange.Count },
        }, ct);

        var export = new AuditExport(AuditExport.FormatName, time.GetUtcNow(), user.UserName, fromIndex, toIndex, AuditExport.AlgorithmText, inRange,
            events.Select(AuditExportEvent.From).ToList());
        return TypedResults.File(JsonSerializer.SerializeToUtf8Bytes(export, ExportJson), "application/json", $"hc360-audit-{fromIndex}-{toIndex}.json");
    }
}
