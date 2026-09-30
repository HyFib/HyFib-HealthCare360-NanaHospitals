using System.Security.Claims;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Realtime;

public enum RealtimeTopicScope
{
    /// <summary>The topic id is a facility: staff may follow only the facility they are signed in to.</summary>
    Facility,

    /// <summary>The topic id is a record (ward, department, patient, session) that the policy's check resolves.</summary>
    Record,
}

/// <summary>Who is asking to follow a topic, as a policy's record check sees it.</summary>
public sealed record RealtimeSubscriber(Guid? UserId, Guid? FacilityId, Guid? PatientId, bool IsPortalUser);

/// <summary>
/// Who may follow the SignalR topics with one prefix (<c>lab:{facilityId}</c>, <c>ward:{wardId}</c>…). Each module
/// registers a policy for the topics it publishes; a topic without a policy cannot be followed.
/// Staff need one of <see cref="Permissions"/> and must be signed in to the topic's facility: compared directly for
/// <see cref="RealtimeTopicScope.Facility"/> topics, and by <see cref="Record"/> (which must check the record's
/// facility) for record topics. Portal users are admitted only where <see cref="AllowPortal"/> is set and
/// <see cref="Record"/> accepts them, typically for their own record.
/// </summary>
public sealed record RealtimeTopicPolicy(
    string Prefix,
    RealtimeTopicScope Scope,
    IReadOnlyList<string> Permissions,
    Func<IServiceProvider, RealtimeSubscriber, Guid, CancellationToken, Task<bool>>? Record = null,
    bool AllowPortal = false);

public static class RealtimeTopicRegistration
{
    public static IServiceCollection AddRealtimeTopic(this IServiceCollection services, RealtimeTopicPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        services.AddSingleton(policy);
        return services;
    }
}

/// <summary>
/// Decides every <c>Subscribe</c> call on the events hub. Deny by default: unknown prefixes, malformed topics,
/// restricted tokens (pending MFA enrolment or password change), missing permissions and other facilities' topics are
/// all refused. Topic payloads carry ids and statuses only, but those still reveal patient activity.
/// </summary>
public sealed class RealtimeTopicAuthorizer
{
    private const int MaxTopicLength = 64;

    private readonly Dictionary<string, RealtimeTopicPolicy> _policies;
    private readonly IPermissionService _permissions;
    private readonly IServiceProvider _services;

    public RealtimeTopicAuthorizer(IEnumerable<RealtimeTopicPolicy> policies, IPermissionService permissions, IServiceProvider services)
    {
        _policies = new Dictionary<string, RealtimeTopicPolicy>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            if (!_policies.TryAdd(policy.Prefix, policy))
            {
                throw new InvalidOperationException($"The realtime topic '{policy.Prefix}' is registered twice.");
            }
        }

        _permissions = permissions;
        _services = services;
    }

    /// <summary>The canonical topic to join when the caller may follow <paramref name="topic"/>; otherwise null.</summary>
    public async Task<string?> AuthorizeAsync(ClaimsPrincipal? user, string? topic, CancellationToken cancellationToken)
    {
        if (user?.Identity?.IsAuthenticated != true
            || !TryParse(topic, out var prefix, out var id)
            || !_policies.TryGetValue(prefix, out var policy)
            || user.HasClaim(c => c.Type == HcClaims.Restriction))
        {
            return null;
        }

        var subscriber = new RealtimeSubscriber(
            ReadGuid(user, HcClaims.UserId),
            ReadGuid(user, HcClaims.Facility),
            ReadGuid(user, HcClaims.Patient),
            string.Equals(user.FindFirst(HcClaims.UserType)?.Value, HcClaims.PatientType, StringComparison.Ordinal));

        var allowed = subscriber.IsPortalUser
            ? policy.AllowPortal && policy.Record is not null && await policy.Record(_services, subscriber, id, cancellationToken)
            : await StaffMayFollowAsync(policy, subscriber, id, cancellationToken);
        return allowed ? $"{prefix}:{id:D}" : null;
    }

    /// <summary>
    /// The facility whose events a connection joins without asking: only a staff session with full access, not a patient
    /// and not a token restricted to a password change or MFA enrolment. Facility events carry patient and record ids.
    /// </summary>
    public static Guid? FacilityToJoin(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true
        && !user.HasClaim(c => c.Type == HcClaims.Restriction)
        && string.Equals(user.FindFirst(HcClaims.UserType)?.Value, HcClaims.StaffType, StringComparison.Ordinal)
        && Guid.TryParse(user.FindFirst(HcClaims.Facility)?.Value, out var facilityId)
            ? facilityId
            : null;

    internal static bool TryParse(string? topic, out string prefix, out Guid id)
    {
        prefix = string.Empty;
        id = Guid.Empty;
        if (string.IsNullOrEmpty(topic) || topic.Length > MaxTopicLength)
        {
            return false;
        }

        var colon = topic.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        prefix = topic[..colon];
        return Guid.TryParseExact(topic[(colon + 1)..], "D", out id);
    }

    private async Task<bool> StaffMayFollowAsync(RealtimeTopicPolicy policy, RealtimeSubscriber subscriber, Guid id, CancellationToken ct)
    {
        if (subscriber.UserId is not { } userId || subscriber.FacilityId is not { } facilityId || !await HoldsAnyAsync(userId, policy.Permissions, ct))
        {
            return false;
        }

        return policy.Scope switch
        {
            RealtimeTopicScope.Facility => id == facilityId,
            RealtimeTopicScope.Record => policy.Record is not null && await policy.Record(_services, subscriber, id, ct),
            _ => false,
        };
    }

    private async Task<bool> HoldsAnyAsync(Guid userId, IReadOnlyList<string> anyOf, CancellationToken ct)
    {
        if (anyOf.Count == 0)
        {
            return true;
        }

        foreach (var permission in anyOf)
        {
            if (await _permissions.HasPermissionAsync(userId, permission, ct))
            {
                return true;
            }
        }

        return false;
    }

    private static Guid? ReadGuid(ClaimsPrincipal user, string claim) => Guid.TryParse(user.FindFirst(claim)?.Value, out var value) ? value : null;
}
