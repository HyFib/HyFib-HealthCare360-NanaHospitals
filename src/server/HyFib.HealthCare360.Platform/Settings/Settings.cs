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

/// <summary>Naming rules for settings that hold credentials.</summary>
public static class SecretSettingKeys
{
    /// <summary>
    /// Keys ending in <c>.secret</c> (<c>integrations.sms.secret</c>, <c>ai.anthropic.secret</c>) hold credentials and are
    /// always stored encrypted, whatever the caller asks for (SRS §5).
    /// </summary>
    public static bool IsSecret(string key) => key.EndsWith(".secret", StringComparison.OrdinalIgnoreCase);
}

public interface ISettingsService
{
    /// <summary>Facility value if present, else the global value, else <c>default</c>.</summary>
    Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the value, encrypted when <paramref name="secret"/> is set or the key is a <see cref="SecretSettingKeys">secret key</see>;
    /// a secret key is never stored in clear text, even when the flag is not passed.
    /// </summary>
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
/// at once for every facility on this server. Other servers on the database hear of it through
/// <see cref="IClusterSignals"/> and expire it at once too (SCAL-01); while a server cannot hear them, it reads every
/// value from the database.
/// </summary>
internal sealed class SettingsService(HospitalDbContext db, IFieldProtector protector, IMemoryCache cache, IClusterSignals signals) : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    public async Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default)
    {
        var json = signals.IsListening
            ? await cache.GetOrCreateAsync($"setting:{key}:{facilityId}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                // Taken before the read: a change saved while the value is being read, here or on another server, expires it
                // at once.
                entry.AddExpirationToken(SettingsCache.ChangeToken(cache, key));
                entry.AddExpirationToken(SettingsCache.AnyChangeToken(cache));
                return await ReadAsync(key, facilityId, cancellationToken);
            })
            : await ReadAsync(key, facilityId, cancellationToken);

        return json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private async Task<string?> ReadAsync(string key, Guid? facilityId, CancellationToken cancellationToken)
    {
        var rows = await db.Set<SystemSetting>().AsNoTracking()
            .Where(s => s.Key == key && (s.FacilityId == null || s.FacilityId == facilityId))
            .ToListAsync(cancellationToken);
        var row = rows.OrderByDescending(s => s.FacilityId != null).FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        return row.IsSecret ? protector.Unprotect(JsonSerializer.Deserialize<string>(row.Value)) : row.Value;
    }

    public Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default) =>
        SetAsync(key, value, facilityId, secret, expectedVersion: null, cancellationToken);

    public async Task SetAsync<T>(string key, T value, Guid? facilityId, bool secret, uint? expectedVersion, CancellationToken cancellationToken = default)
    {
        // The flag alone is not trusted: a script, an older admin screen or a module that forgets it would otherwise leave
        // credentials in clear text in the database and in every backup.
        secret |= SecretSettingKeys.IsSecret(key);
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

    /// <summary>Expires every cached value of the key, here and on every other server on the database.</summary>
    private void Changed(string key)
    {
        SettingsCache.Expire(cache, key);
        signals.Publish(SettingsCache.SignalTopic, key);
    }
}

/// <summary>
/// The change tokens cached setting values depend on: one per key, and one for all of them. Another server's change to a
/// key expires it here (<see cref="IClusterCache"/>), and starting to hear the other servers again expires them all.
/// </summary>
internal sealed class SettingsCache(IMemoryCache cache) : IClusterCache
{
    public const string SignalTopic = "setting";
    private const string AnyKey = "setting-changed:*";

    public string Topic => SignalTopic;

    public void Expire(string? argument) => Expire(cache, argument);

    public static CancellationChangeToken ChangeToken(IMemoryCache cache, string key) => Token(cache, TokenKey(key));

    public static CancellationChangeToken AnyChangeToken(IMemoryCache cache) => Token(cache, AnyKey);

    /// <summary>
    /// Expires every cached value of <paramref name="key"/>, or of every key when it is null. The source is not disposed: a
    /// reader may still be taking its token.
    /// </summary>
    public static void Expire(IMemoryCache cache, string? key)
    {
        var tokenKey = key is null ? AnyKey : TokenKey(key);
        if (cache.TryGetValue(tokenKey, out CancellationTokenSource? source) && source is not null)
        {
            cache.Remove(tokenKey);
            source.Cancel();
        }
    }

    private static string TokenKey(string key) => $"setting-changed:{key}";

    private static CancellationChangeToken Token(IMemoryCache cache, string tokenKey) =>
        new(cache.GetOrCreate(tokenKey, entry =>
        {
            entry.Priority = CacheItemPriority.NeverRemove;
            return new CancellationTokenSource();
        })!.Token);
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
            // A secret key saved in clear text before encryption was enforced is still never shown.
            return TypedResults.Ok(rows.Select(s => (Row: s, Secret: s.IsSecret || SecretSettingKeys.IsSecret(s.Key))).Select(x => new SettingDto(
                x.Row.Id, x.Row.Key, x.Row.FacilityId, x.Secret ? null : JsonNode.Parse(x.Row.Value), x.Secret, x.Row.Value != "null", x.Row.Description,
                x.Row.UpdatedAt ?? x.Row.CreatedAt, x.Row.Version)));
        });

        admin.MapPut("/{key}", async (string key, SaveSettingRequest request, ISettingsService settings, IAuditLogger audit, HospitalDbContext db, CancellationToken ct) =>
        {
            if (key.Length is 0 or > 128 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return Error.Field("key", "Use letters, digits, dots, dashes or underscores.").ToProblem();
            }

            // A setting with rules of its own is refused here, not read leniently later (a default, or the nearest value allowed).
            if (AntiAutomationSettings.Problem(key, request.Value) is { } problem)
            {
                return Error.Field("value", problem).ToProblem();
            }

            // SetAsync encrypts *.secret keys even when the request leaves "secret" unset.
            var secret = request.Secret || SecretSettingKeys.IsSecret(key);
            await settings.SetAsync(key, request.Value, request.FacilityId, secret, request.Version, ct);
            if (request.Description is not null)
            {
                await db.Set<SystemSetting>().Where(s => s.Key == key && s.FacilityId == request.FacilityId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Description, request.Description), ct);
            }

            await audit.WriteAsync(new AuditEntry(AuditCategories.Admin, "setting.changed", $"Changed setting {key}")
            {
                EntityType = nameof(SystemSetting),
                EntityId = key,
                Details = secret ? new { secret = true } : request.Value,
                Severity = key.StartsWith("integrations.", StringComparison.Ordinal) || secret ? AuditSeverity.Warning : AuditSeverity.Info,
            }, ct);
            return TypedResults.NoContent();
        });
    }
}
