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

public sealed class PermissionAuthorizationHandler(IPermissionService permissions) : AuthorizationHandler<PermissionRequirement>
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
        if (granted.Contains(PermissionRegistry.Wildcard) || requirement.AnyOf.Any(granted.Contains))
        {
            context.Succeed(requirement);
        }
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

    void Invalidate(Guid userId);

    void InvalidateAll();
}

internal sealed class PermissionService(IMemoryCache cache, IServiceScopeFactory scopeFactory) : IPermissionService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
    private int _generation;

    public async ValueTask<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(userId);
        if (cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached is not null)
        {
            return cached;
        }

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

        cache.Set(key, permissions, CacheDuration);
        return permissions;
    }

    public async ValueTask<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default)
    {
        var granted = await GetPermissionsAsync(userId, cancellationToken);
        return granted.Contains(PermissionRegistry.Wildcard) || granted.Contains(permission);
    }

    public void Invalidate(Guid userId) => cache.Remove(CacheKey(userId));

    public void InvalidateAll() => Interlocked.Increment(ref _generation);

    private string CacheKey(Guid userId) => $"perm:{Volatile.Read(ref _generation)}:{userId:N}";
}

/// <summary>Rejects access tokens whose session was revoked (sign-out, reuse detection, admin revoke).</summary>
public interface ISessionValidator
{
    ValueTask<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default);

    void Invalidate(Guid sessionId);
}

internal sealed class SessionValidator(IMemoryCache cache, IServiceScopeFactory scopeFactory, TimeProvider time) : ISessionValidator
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async ValueTask<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var key = $"session:{sessionId:N}";
        if (cache.TryGetValue(key, out bool active))
        {
            return active;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = time.GetUtcNow();
        active = await db.Set<UserSession>().AnyAsync(s => s.Id == sessionId && s.RevokedAt == null && s.ExpiresAt > now, cancellationToken);

        cache.Set(key, active, CacheDuration);
        return active;
    }

    public void Invalidate(Guid sessionId) => cache.Remove($"session:{sessionId:N}");
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
