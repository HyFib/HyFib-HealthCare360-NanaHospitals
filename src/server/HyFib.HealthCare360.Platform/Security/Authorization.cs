using System.Collections.Concurrent;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>Satisfied when the user holds any one of the listed permissions.</summary>
public sealed class PermissionRequirement(IReadOnlyList<string> anyOf) : IAuthorizationRequirement
{
    public IReadOnlyList<string> AnyOf { get; } = anyOf;
}

/// <summary>Builds policies named <c>perm:a|b</c> on demand, so endpoints never need pre-registered policies.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _cache = new(StringComparer.Ordinal);

    public override Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return base.GetPolicyAsync(policyName);
        }

        var policy = _cache.GetOrAdd(policyName, static name =>
        {
            var permissions = name[Prefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permissions))
                .Build();
        });

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <remarks>
/// A request that only an administrative permission allows must come from an enrolled HC360 Desktop
/// (<see cref="AdministrationOnDesktop"/>, SRS SEC-08); any other permission the requirement names is enough anywhere.
/// </remarks>
public sealed class PermissionAuthorizationHandler(IPermissionService permissions, IAdministrationDesktops desktops) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!Guid.TryParse(context.User.FindFirst(HcClaims.UserId)?.Value, out var userId)
            || context.User.HasClaim(c => c.Type == HcClaims.Restriction))
        {
            // Restricted tokens (pending MFA enrolment / password change) grant no permissions.
            return;
        }

        var granted = await permissions.GetPermissionsAsync(userId);
        var held = granted.Contains(PermissionRegistry.Wildcard) ? requirement.AnyOf : requirement.AnyOf.Where(granted.Contains).ToList();
        if (held.Count == 0)
        {
            return;
        }

        if (held.Any(p => !PlatformPermissions.IsAdministrative(p))
            || AdministrationOnDesktop.OnDesktop(context.User)
            || !await desktops.RequiredAsync()
            || (FirstDesktopEnrolment.Marks(context.Resource) && !await desktops.EnrolledAsync()))
        {
            context.Succeed(requirement);
            return;
        }

        context.Fail(new DesktopRequired(this));
    }
}

/// <summary>
/// Resolves a user's effective permissions from their roles. Results are cached briefly so that
/// permission changes take effect within seconds without putting permissions in the token.
/// </summary>
public interface IPermissionService
{
    ValueTask<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default);

    ValueTask<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the user's permissions here and on every other server. Call it once the change is saved, as
    /// <see cref="ISessionValidator.Invalidate"/>.
    /// </summary>
    void Invalidate(Guid userId);

    /// <summary>Forgets everybody's permissions here and on every other server, once the change is saved.</summary>
    void InvalidateAll();
}

/// <remarks>
/// With several servers on the database (SCAL-01) a change on one reaches the others at once through
/// <see cref="IClusterSignals"/>, and nothing is taken from the cache while this server cannot hear them. A read that was
/// under way when a change arrived is not kept: it may have read the permissions from before the change.
/// </remarks>
internal sealed class PermissionService(IMemoryCache cache, IServiceScopeFactory scopeFactory, IClusterSignals signals) : IPermissionService, IClusterCache
{
    public const string SignalTopic = "permissions";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
    private int _generation;
    private long _changes;

    public string Topic => SignalTopic;

    public async ValueTask<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var coherent = signals.IsListening;
        var key = CacheKey(userId);
        if (coherent && cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached is not null)
        {
            return cached;
        }

        var changes = Interlocked.Read(ref _changes);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

        var active = await db.Set<User>().AnyAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        IReadOnlySet<string> permissions = active
            ? (await (from ur in db.Set<UserRole>()
                      join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                      where ur.UserId == userId
                      select rp.Permission)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>();

        if (coherent && Interlocked.Read(ref _changes) == changes)
        {
            cache.Set(key, permissions, CacheDuration);
        }

        return permissions;
    }

    public async ValueTask<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default)
    {
        var granted = await GetPermissionsAsync(userId, cancellationToken);
        return granted.Contains(PermissionRegistry.Wildcard) || granted.Contains(permission);
    }

    public void Invalidate(Guid userId)
    {
        Expire(userId.ToString("N"));
        signals.Publish(SignalTopic, userId.ToString("N"));
    }

    public void InvalidateAll()
    {
        Expire(null);
        signals.Publish(SignalTopic);
    }

    /// <summary>Forgets one user's permissions, or everybody's: here, or because another server changed them.</summary>
    public void Expire(string? argument)
    {
        Interlocked.Increment(ref _changes);
        if (argument is null)
        {
            Interlocked.Increment(ref _generation);
        }
        else if (Guid.TryParseExact(argument, "N", out var userId))
        {
            cache.Remove(CacheKey(userId));
        }
    }

    private string CacheKey(Guid userId) => $"perm:{Volatile.Read(ref _generation)}:{userId:N}";
}

/// <summary>Where a session stands for the requests made with its access tokens.</summary>
public enum SessionState
{
    /// <summary>Signed in and unlocked.</summary>
    Active,

    /// <summary>The screen is locked: only unlocking, signing out and reading who is signed in are allowed.</summary>
    Locked,

    /// <summary>Signed out, expired, or ended by the server or an administrator.</summary>
    Ended,
}

/// <summary>
/// Checks the session behind each access token, so a session that ended stops working at once and a locked one can only
/// be unlocked (sign-out, reuse detection, an administrator, the session limit, the idle lock).
/// </summary>
public interface ISessionValidator
{
    ValueTask<SessionState> GetStateAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets what this server and every other one cached about the session. Call it once the change to the session is
    /// saved (committed), never before: another server told sooner may read the session as it was and cache that, and
    /// nothing tells it again (SCAL-01).
    /// </summary>
    void Invalidate(Guid sessionId);
}

/// <remarks>
/// Every change to a session clears what is cached about it (<see cref="Invalidate"/>), here and, through
/// <see cref="IClusterSignals"/>, on every other server on the database (SCAL-01), so an active session can be kept for a
/// while. While this server cannot hear the others, an active session is read every time: a session locked or ended on
/// another server must stop working here at once. An ended session never comes back, so it is always kept.
/// </remarks>
internal sealed class SessionValidator(IMemoryCache cache, IServiceScopeFactory scopeFactory, TimeProvider time, IClusterSignals signals)
    : ISessionValidator, IClusterCache
{
    public const string SignalTopic = "session";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private int _generation;
    private long _changes;

    public string Topic => SignalTopic;

    public async ValueTask<SessionState> GetStateAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var coherent = signals.IsListening;
        var key = CacheKey(sessionId);
        if (cache.TryGetValue(key, out SessionState cached) && (coherent || cached == SessionState.Ended))
        {
            return cached;
        }

        var changes = Interlocked.Read(ref _changes);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = time.GetUtcNow();
        var session = await db.Set<UserSession>().AsNoTracking()
            .Where(s => s.Id == sessionId && s.RevokedAt == null && s.ExpiresAt > now)
            .Select(s => new { s.LockedAt })
            .FirstOrDefaultAsync(cancellationToken);
        var state = session is null ? SessionState.Ended : session.LockedAt is null ? SessionState.Active : SessionState.Locked;

        // A locked session is read every time, so unlocking it takes effect at once on every server. It makes few
        // requests: the locked app does not fetch. A read that was under way when a change arrived is not kept: it may
        // have read the session from before the change.
        if (state == SessionState.Ended || (state == SessionState.Active && coherent && Interlocked.Read(ref _changes) == changes))
        {
            cache.Set(key, state, CacheDuration);
        }

        return state;
    }

    public void Invalidate(Guid sessionId)
    {
        Expire(sessionId.ToString("N"));
        signals.Publish(SignalTopic, sessionId.ToString("N"));
    }

    /// <summary>Forgets what is cached about one session, or every session: here, or because another server changed it.</summary>
    public void Expire(string? argument)
    {
        Interlocked.Increment(ref _changes);
        if (argument is null)
        {
            Interlocked.Increment(ref _generation);
        }
        else if (Guid.TryParseExact(argument, "N", out var sessionId))
        {
            cache.Remove(CacheKey(sessionId));
        }
    }

    private string CacheKey(Guid sessionId) => $"session:{Volatile.Read(ref _generation)}:{sessionId:N}";
}

public static class EndpointAuthorizationExtensions
{
    /// <summary>Requires the named permission (deny by default).</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.Prefix + permission);

    /// <summary>Requires at least one of the permissions.</summary>
    public static TBuilder RequireAnyPermission<TBuilder>(this TBuilder builder, params string[] permissions)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PermissionPolicyProvider.Prefix + string.Join('|', permissions));
}
