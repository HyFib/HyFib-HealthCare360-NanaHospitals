using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Notifications;

public sealed record UserNotificationDto(Guid Id, string Title, string? Body, string? Link, string Category, string Severity, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationTemplateDto(
    Guid Id, Guid? FacilityId, string Key, NotificationChannel Channel, string Language, string? Subject, string Body, string? DltTemplateId,
    string? WhatsAppTemplateName, string? Description, bool IsActive, uint Version, bool WhatsAppDocumentHeader);

/// <param name="WhatsAppDocumentHeader">The WhatsApp template was approved with a document header (see <see cref="NotificationTemplate.WhatsAppDocumentHeader"/>).</param>
public sealed record SaveNotificationTemplateRequest(
    string Key, NotificationChannel Channel, string Language, Guid? FacilityId, string? Subject, string Body, string? DltTemplateId,
    string? WhatsAppTemplateName, string? Description, bool IsActive, uint? Version, bool WhatsAppDocumentHeader = false);

public sealed record NotificationLogDto(
    Guid Id, DateTimeOffset CreatedAt, NotificationChannel Channel, string TemplateKey, string? Recipient, Guid? PatientId,
    NotificationStatus Status, string? Error, DateTimeOffset? SentAt);

internal static class NotificationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/v1/notifications").WithTags("Notifications").RequireAuthorization();

        mine.MapGet("/", async ([AsParameters] PageQuery query, bool? unreadOnly, HospitalDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var page = query.ToRequest();
            var items = db.Set<UserNotification>().Where(n => n.UserId == user.UserId);
            if (unreadOnly == true)
            {
                items = items.Where(n => n.ReadAt == null);
            }

            return TypedResults.Ok(await items.OrderByDescending(n => n.CreatedAt)
                .Select(n => new UserNotificationDto(n.Id, n.Title, n.Body, n.Link, n.Category, n.Severity, n.CreatedAt, n.ReadAt))
                .ToPagedResultAsync(page, ct));
        });

        mine.MapGet("/unread-count", async (HospitalDbContext db, ICurrentUser user, CancellationToken ct) =>
            TypedResults.Ok(new { count = await db.Set<UserNotification>().CountAsync(n => n.UserId == user.UserId && n.ReadAt == null, ct) }));

        mine.MapPost("/{id:guid}/read", async (Guid id, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
        {
            await db.Set<UserNotification>().Where(n => n.Id == id && n.UserId == user.UserId && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, time.GetUtcNow()), ct);
            return TypedResults.NoContent();
        });

        mine.MapPost("/read-all", async (HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct) =>
        {
            await db.Set<UserNotification>().Where(n => n.UserId == user.UserId && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, time.GetUtcNow()), ct);
            return TypedResults.NoContent();
        });

        var admin = app.MapGroup("/api/v1/admin").WithTags("Administration");

        admin.MapGet("/notification-templates", async (string? key, NotificationChannel? channel, HospitalDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await db.Set<NotificationTemplate>()
                    .Where(t => (key == null || t.Key == key) && (channel == null || t.Channel == channel))
                    .OrderBy(t => t.Key).ThenBy(t => t.Channel).ThenBy(t => t.Language)
                    .Select(t => new NotificationTemplateDto(t.Id, t.FacilityId, t.Key, t.Channel, t.Language, t.Subject, t.Body, t.DltTemplateId,
                        t.WhatsAppTemplateName, t.Description, t.IsActive, t.Version, t.WhatsAppDocumentHeader))
                    .ToListAsync(ct)))
            .RequirePermission(PlatformPermissions.NotificationTemplatesManage);

        admin.MapPost("/notification-templates", (SaveNotificationTemplateRequest r, HospitalDbContext db, CancellationToken ct) => SaveTemplate(null, r, db, ct))
            .RequirePermission(PlatformPermissions.NotificationTemplatesManage);

        admin.MapPut("/notification-templates/{id:guid}", (Guid id, SaveNotificationTemplateRequest r, HospitalDbContext db, CancellationToken ct) => SaveTemplate(id, r, db, ct))
            .RequirePermission(PlatformPermissions.NotificationTemplatesManage);

        admin.MapGet("/notification-logs", async ([AsParameters] PageQuery query, NotificationChannel? channel, NotificationStatus? status, HospitalDbContext db, CancellationToken ct) =>
            {
                var page = query.ToRequest();
                var logs = db.Set<NotificationLog>().AsNoTracking();
                if (channel is not null)
                {
                    logs = logs.Where(l => l.Channel == channel);
                }

                if (status is not null)
                {
                    logs = logs.Where(l => l.Status == status);
                }

                if (page.SearchTerm is { } term)
                {
                    logs = logs.Where(l => l.TemplateKey.Contains(term));
                }

                return TypedResults.Ok(await logs.OrderByDescending(l => l.CreatedAt)
                    .Select(l => new NotificationLogDto(l.Id, l.CreatedAt, l.Channel, l.TemplateKey, l.Recipient, l.PatientId, l.Status, l.Error, l.SentAt))
                    .ToPagedResultAsync(page, ct));
            })
            .RequirePermission(PlatformPermissions.NotificationTemplatesManage);
    }

    private static async Task<IResult> SaveTemplate(Guid? id, SaveNotificationTemplateRequest request, HospitalDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.Body))
        {
            return Error.Validation("template.invalid", "Key and body are required.", new Dictionary<string, string[]>
            {
                ["body"] = ["Body is required."],
            }).ToProblem();
        }

        if (request.Channel == NotificationChannel.Sms && request.Body.Length > 1000)
        {
            return Error.Field("body", "SMS templates must be at most 1000 characters.").ToProblem();
        }

        NotificationTemplate template;
        if (id is { } existingId)
        {
            var found = await db.Set<NotificationTemplate>().FirstOrDefaultAsync(t => t.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(NotificationTemplate), existingId).ToProblem();
            }

            template = found;
            if (request.Version is { } v)
            {
                db.SetExpectedVersion(template, v);
            }
        }
        else
        {
            template = new NotificationTemplate
            {
                Key = request.Key.Trim(),
                Channel = request.Channel,
                Language = string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language.Trim().ToLowerInvariant(),
                FacilityId = request.FacilityId,
            };
            db.Add(template);
        }

        template.Subject = request.Subject?.Trim();
        template.Body = request.Body.Trim();
        template.DltTemplateId = request.DltTemplateId?.Trim();
        template.WhatsAppTemplateName = request.WhatsAppTemplateName?.Trim();
        template.WhatsAppDocumentHeader = request.Channel == NotificationChannel.WhatsApp && request.WhatsAppDocumentHeader;
        template.Description = request.Description?.Trim();
        template.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/admin/notification-templates/{template.Id}", new { template.Id }) : TypedResults.NoContent();
    }
}
