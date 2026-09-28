using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
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
                events = events.Where(e => EF.Functions.ILike(e.Action, action.Trim().ToLikePattern()));
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
                events = events.Where(e => (e.Summary != null && EF.Functions.ILike(e.Summary, like)) || (e.UserName != null && EF.Functions.ILike(e.UserName, like)));
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

        group.MapGet("/verify", async (AuditChainService chain, IAuditLogger audit, CancellationToken ct) =>
        {
            var result = await chain.VerifyAsync(ct);
            await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "audit.verified", result.Message)
            {
                Severity = result.Valid ? AuditSeverity.Info : AuditSeverity.Critical,
            }, ct);
            return TypedResults.Ok(result);
        });
    }
}
