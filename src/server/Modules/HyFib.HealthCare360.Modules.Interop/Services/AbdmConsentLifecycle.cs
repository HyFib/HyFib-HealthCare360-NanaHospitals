using HyFib.HealthCare360.Modules.Interop.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// When a consent's status may change. A consent that has ended (revoked, expired or denied) stays ended: the consent
/// manager can send a granted notification late or twice, and it must not bring the consent back.
/// </summary>
internal static class AbdmConsentLifecycle
{
    /// <summary>Whether a consent status, as the consent manager names it, is an ending one.</summary>
    internal static bool HasEnded(string? status) =>
        status is not null
        && (status.Equals("REVOKED", StringComparison.OrdinalIgnoreCase)
            || status.Equals("EXPIRED", StringComparison.OrdinalIgnoreCase)
            || status.Equals("DENIED", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a consent request's status is an ending one.</summary>
    internal static bool HasEnded(AbdmConsentRequestStatus status) =>
        status is AbdmConsentRequestStatus.Revoked or AbdmConsentRequestStatus.Expired or AbdmConsentRequestStatus.Denied;

    /// <summary>Whether a consent in the <paramref name="current"/> status may take the <paramref name="next"/> one: never out of an ended one.</summary>
    internal static bool MayChange(string? current, string? next) => !HasEnded(current) || string.Equals(current, next, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a consent request in the <paramref name="current"/> status may take the <paramref name="next"/> one: never out of an ended one.</summary>
    internal static bool MayChange(AbdmConsentRequestStatus current, AbdmConsentRequestStatus next) => !HasEnded(current) || current == next;
}
