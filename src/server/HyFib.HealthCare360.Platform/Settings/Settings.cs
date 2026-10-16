using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Features;
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
using Microsoft.Extensions.Primitives;

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

    /// <summary>
    /// Saves the value as <see cref="SetAsync{T}(string, T, Guid?, bool, CancellationToken)"/> does, unless someone else
    /// changed it since the caller read it at <paramref name="expectedVersion"/> (its <see cref="SystemSetting.Version"/>):
    /// then nothing is saved and a <see cref="DbUpdateConcurrencyException"/> (409) is thrown, so the last writer does not
    /// silently win (REL-04). Without an expected version it saves whatever is there, as before.
    /// </summary>
    /// <exception cref="DbUpdateConcurrencyException">The value changed, or was removed, since it was read.</exception>
    Task SetAsync<T>(string key, T value, Guid? facilityId, bool secret, uint? expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Removes the value of <paramref name="key"/> for the facility (or the global one). False when there was none.</summary>
    Task<bool> RemoveAsync(string key, Guid? facilityId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Values are cached for a minute per key and facility. Every cached value of a key (the hospital-wide one and each
/// facility's merged one) depends on one change token, so saving the key, hospital-wide or for a facility, takes effect
/// at once for every facility on this server; other servers see it within the minute.
/// </summary>
internal sealed class SettingsService(HospitalDbContext db, IFieldProtector protector, IMemoryCache cache) : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public async Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default)
    {
        var json = await cache.GetOrCreateAsync($"setting:{key}:{facilityId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            // Taken before the read: a change saved while the value is being read expires it at once.
            entry.AddExpirationToken(ChangeToken(key));
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

    public Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default) =>
        SetAsync(key, value, facilityId, secret, expectedVersion: null, cancellationToken);

    public async Task SetAsync<T>(string key, T value, Guid? facilityId, bool secret, uint? expectedVersion, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var stored = secret ? JsonSerializer.Serialize(protector.Protect(json)) : json;

        var row = await db.Set<SystemSetting>().FirstOrDefaultAsync(s => s.Key == key && s.FacilityId == facilityId, cancellationToken);
        if (expectedVersion is { } expected)
        {
            if (row is null)
            {
                throw new DbUpdateConcurrencyException($"Setting {key} was removed after it was read.");
            }

            db.SetExpectedVersion(row, expected);
        }

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
        Changed(key);
    }

    public async Task<bool> RemoveAsync(string key, Guid? facilityId, CancellationToken cancellationToken = default)
    {
        var removed = await db.Set<SystemSetting>().Where(s => s.Key == key && s.FacilityId == facilityId).ExecuteDeleteAsync(cancellationToken);
        Changed(key);
        return removed > 0;
    }

    private static string TokenKey(string key) => $"setting-changed:{key}";

    private CancellationChangeToken ChangeToken(string key) =>
        new(cache.GetOrCreate(TokenKey(key), entry =>
        {
            entry.Priority = CacheItemPriority.NeverRemove;
            return new CancellationTokenSource();
        })!.Token);

    /// <summary>Expires every cached value of the key. The source is not disposed: a reader may still be taking its token.</summary>
    private void Changed(string key)
    {
        if (cache.TryGetValue(TokenKey(key), out CancellationTokenSource? source) && source is not null)
        {
            cache.Remove(TokenKey(key));
            source.Cancel();
        }
    }
}

/// <param name="Version">The row's version, to send back with a change (<see cref="SaveSettingRequest.Version"/>).</param>
public sealed record SettingDto(
    Guid Id, string Key, Guid? FacilityId, JsonNode? Value, bool IsSecret, bool HasValue, string? Description, DateTimeOffset? UpdatedAt, uint Version);

/// <param name="Version">
/// The version of the value the change was made to (<see cref="SettingDto.Version"/>): 409 when it has changed since.
/// Omit it for a value that was not set when it was read.
/// </param>
public sealed record SaveSettingRequest(JsonNode? Value, Guid? FacilityId, bool Secret, string? Description, uint? Version = null);

internal static class SettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Any signed-in user may read non-secret "ui.*" (branding) and "features.*" (feature flags) settings. Every switch
        // in the feature catalogue is given as the server enforces it for the user's facility (default and fallbacks
        // applied), so the apps hide exactly what the server refuses.
        app.MapGet("/api/v1/settings/public", async (HospitalDbContext db, ICurrentUser user, IFeatureFlags features, CancellationToken ct) =>
            {
                var rows = await db.Set<SystemSetting>().AsNoTracking()
                    .Where(s => !s.IsSecret && (s.Key.StartsWith("ui.") || s.Key.StartsWith("features."))
                                && (s.FacilityId == null || s.FacilityId == user.FacilityId))
                    .ToListAsync(ct);
                var merged = rows.OrderBy(r => r.FacilityId != null)
                    .GroupBy(r => r.Key)
                    .ToDictionary(g => g.Key, g => JsonNode.Parse(g.Last().Value));
                foreach (var feature in FeatureCatalog.All)
                {
                    merged[feature.Key] = JsonValue.Create(await features.IsEnabledAsync(feature.Key, user.FacilityId, ct));
                }

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
                s.Id, s.Key, s.FacilityId, s.IsSecret ? null : JsonNode.Parse(s.Value), s.IsSecret, s.Value != "null", s.Description, s.UpdatedAt ?? s.CreatedAt,
                s.Version)));
        });

        admin.MapPut("/{key}", async (string key, SaveSettingRequest request, ISettingsService settings, IAuditLogger audit, HospitalDbContext db, CancellationToken ct) =>
        {
            if (key.Length is 0 or > 128 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return Error.Field("key", "Use letters, digits, dots, dashes or underscores.").ToProblem();
            }

            await settings.SetAsync(key, request.Value, request.FacilityId, request.Secret, request.Version, ct);
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
