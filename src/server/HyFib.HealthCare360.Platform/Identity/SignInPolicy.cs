using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// The rules of signing in that need no database (ASVS V6.3.1, V6.3.3): who must use two-step verification, and how
/// wrong passwords and authenticator codes lock an account.
/// </summary>
internal static class SignInPolicy
{
    /// <summary>
    /// Two-step verification is required of the roles in <see cref="SecurityOptions.MfaRequiredRoles"/> and, with
    /// <see cref="SecurityOptions.MfaForAdministration"/>, of anyone holding an administrative permission, whatever their
    /// roles are called.
    /// </summary>
    public static bool RequiresMfa(SecurityOptions security, IEnumerable<string> roles, IEnumerable<string> permissions) =>
        security.EnforceMfa
        && (roles.Any(r => security.MfaRequiredRoles.Contains(r, StringComparer.OrdinalIgnoreCase))
            || (security.MfaForAdministration && permissions.Any(PlatformPermissions.IsAdministrative)));

    /// <summary>
    /// Counts a wrong password or authenticator code against the account. Every <see cref="SecurityOptions.MaxFailedLogins"/>
    /// in a row lock it, each lock twice as long as the one before, up to 24 hours. Failures are forgotten after
    /// <see cref="SecurityOptions.FailedLoginResetHours"/> without another, and the count starts again.
    /// </summary>
    /// <returns>True when this failure locked the account.</returns>
    public static bool RecordFailure(SecurityOptions security, User user, DateTimeOffset now)
    {
        if (security.FailedLoginResetHours > 0 && user.LastFailedLoginAt is { } last && now - last >= TimeSpan.FromHours(security.FailedLoginResetHours))
        {
            user.FailedLoginCount = 0;
        }

        user.FailedLoginCount++;
        user.LastFailedLoginAt = now;
        if (user.FailedLoginCount % security.MaxFailedLogins != 0)
        {
            return false;
        }

        var multiplier = Math.Pow(2, (user.FailedLoginCount / security.MaxFailedLogins) - 1);
        user.LockoutEndsAt = now + TimeSpan.FromMinutes(Math.Min(security.LockoutMinutes * multiplier, 24 * 60));
        return true;
    }
}
