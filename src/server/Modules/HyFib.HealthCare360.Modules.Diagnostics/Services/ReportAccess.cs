namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Who may see unreleased diagnostic work. The performing department (entry/verification permissions) works on drafts;
/// clinicians and nurses with read-only access see released reports only. An unverified value must never guide
/// treatment.
/// </summary>
internal static class ReportAccess
{
    public static async Task<bool> ReleasedOnlyAsync(
        ICurrentUser user, IPermissionService permissions, IReadOnlyList<string> departmentPermissions, CancellationToken ct)
    {
        if (user.UserId is not { } userId)
        {
            return true;
        }

        foreach (var permission in departmentPermissions)
        {
            if (await permissions.HasPermissionAsync(userId, permission, ct))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"R. K." — enough to confirm a printed report on the public verification page without disclosing identity.</summary>
    public static string Initials(string? firstName, string? lastName)
    {
        static string Initial(string? name) => string.IsNullOrWhiteSpace(name) ? string.Empty : $"{char.ToUpperInvariant(name.Trim()[0])}.";
        return string.Join(' ', new[] { Initial(firstName), Initial(lastName) }.Where(i => i.Length > 0));
    }
}
