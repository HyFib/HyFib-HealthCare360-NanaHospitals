using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Caching.Memory;

namespace HyFib.HealthCare360.Platform.Settings;

/// <summary>
/// Runtime-configurable setting (JSON). Facility rows override global rows. Secret settings (integration
/// credentials) are encrypted with <see cref="IFieldProtector"/> and never returned in clear text.
/// Keys use dotted names: <c>integrations.sms</c>, <c>integrations.sms.secret</c>, <c>ui.theme</c>, <c>features.telehealth</c>.
/// </summary>
public sealed class SystemSetting : AuditableEntity
{
    public Guid? FacilityId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = "null";
    public bool IsSecret { get; set; }
    public string? Description { get; set; }
}

internal sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> b)
    {
        b.ToTable("system_settings", "platform");
        b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.Value).HasColumnType("jsonb");
        b.Property(x => x.Description).HasMaxLength(300);
        b.HasIndex(x => new { x.Key, x.FacilityId }).IsUnique().AreNullsDistinct(false);
    }
}

public interface ISettingsService
{
    /// <summary>Facility value if present, else the global value, else <c>default</c>.</summary>
    Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default);
}

internal sealed class SettingsService(HospitalDbContext db, IFieldProtector protector, IMemoryCache cache) : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public async Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default)
    {
        var json = await cache.GetOrCreateAsync($"setting:{key}:{facilityId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            var rows = await db.Set<SystemSetting>().AsNoTracking()
                .Where(s => s.Key == key && (s.FacilityId == null || s.FacilityId == facilityId))
                .ToListAsync(cancellationToken);
            var row = rows.OrderByDescending(s => s.FacilityId != null).FirstOrDefault();
            if (row is null)
            {
                return null;
            }

            return row.IsSecret ? protector.Unprotect(JsonSerializer.Deserialize<string>(row.Value)) : row.Value;
        });

        return json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var stored = secret ? JsonSerializer.Serialize(protector.Protect(json)) : json;

        var row = await db.Set<SystemSetting>().FirstOrDefaultAsync(s => s.Key == key && s.FacilityId == facilityId, cancellationToken);
        if (row is null)
        {
            db.Add(new SystemSetting { Key = key, FacilityId = facilityId, Value = stored, IsSecret = secret });
        }
        else
        {
            row.Value = stored;
            row.IsSecret = secret;
        }

        await db.SaveChangesAsync(cancellationToken);
        cache.Remove($"setting:{key}:{facilityId}");
        cache.Remove($"setting:{key}:");
    }
}

public sealed record SettingDto(Guid Id, string Key, Guid? FacilityId, JsonNode? Value, bool IsSecret, bool HasValue, string? Description, DateTimeOffset? UpdatedAt);

public sealed record SaveSettingRequest(JsonNode? Value, Guid? FacilityId, bool Secret, string? Description);

internal static class SettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Any signed-in user may read non-secret "ui.*" (branding) and "features.*" (feature flags) settings.
        app.MapGet("/api/v1/settings/public", async (HospitalDbContext db, ICurrentUser user, CancellationToken ct) =>
            {
                var rows = await db.Set<SystemSetting>().AsNoTracking()
                    .Where(s => !s.IsSecret && (s.Key.StartsWith("ui.") || s.Key.StartsWith("features."))
                                && (s.FacilityId == null || s.FacilityId == user.FacilityId))
                    .ToListAsync(ct);
                var merged = rows.OrderBy(r => r.FacilityId != null)
                    .GroupBy(r => r.Key)
                    .ToDictionary(g => g.Key, g => JsonNode.Parse(g.Last().Value));
                return TypedResults.Ok(merged);
            })
            .WithTags("Settings")
            .RequireAuthorization();

        var admin = app.MapGroup("/api/v1/admin/settings").WithTags("Administration").RequirePermission(PlatformPermissions.SettingsManage);

        admin.MapGet("/", async (string? prefix, HospitalDbContext db, CancellationToken ct) =>
        {
            var query = db.Set<SystemSetting>().AsNoTracking();
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                query = query.Where(s => s.Key.StartsWith(prefix));
            }

            var rows = await query.OrderBy(s => s.Key).ToListAsync(ct);
            return TypedResults.Ok(rows.Select(s => new SettingDto(
                s.Id, s.Key, s.FacilityId, s.IsSecret ? null : JsonNode.Parse(s.Value), s.IsSecret, s.Value != "null", s.Description, s.UpdatedAt ?? s.CreatedAt)));
        });

        admin.MapPut("/{key}", async (string key, SaveSettingRequest request, ISettingsService settings, IAuditLogger audit, HospitalDbContext db, CancellationToken ct) =>
        {
            if (key.Length is 0 or > 128 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return Error.Field("key", "Use letters, digits, dots, dashes or underscores.").ToProblem();
            }

            await settings.SetAsync(key, request.Value, request.FacilityId, request.Secret, ct);
            if (request.Description is not null)
            {
                await db.Set<SystemSetting>().Where(s => s.Key == key && s.FacilityId == request.FacilityId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, request.Description), ct);
            }

            await audit.WriteAsync(new AuditEntry(AuditCategories.Admin, "setting.changed", $"Changed setting {key}")
            {
                EntityType = nameof(SystemSetting),
                EntityId = key,
                Details = request.Secret || key.EndsWith(".secret", StringComparison.Ordinal) ? new { secret = true } : request.Value,
                Severity = key.StartsWith("integrations.", StringComparison.Ordinal) || request.Secret ? AuditSeverity.Warning : AuditSeverity.Info,
            }, ct);
            return TypedResults.NoContent();
        });
    }
}
