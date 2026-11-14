using HyFib.HealthCare360.Platform.Identity;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// Who holds a permission through their roles, for sending them work and notifications: the same answer
/// <see cref="IPermissionService"/> gives about one person, for everyone at once.
/// </summary>
public static class PermissionHolders
{
    /// <summary>
    /// The active members of staff who hold <paramref name="permission"/>, or all-access, through a role in use
    /// (<see cref="Role.IsActive"/>). A role turned off sends its holders nothing, as it gives them nothing.
    /// </summary>
    public static IQueryable<User> Of(DbContext db, string permission)
    {
        ArgumentNullException.ThrowIfNull(db);
        var roleIds = db.Set<RolePermission>()
            .Where(rp => rp.Permission == permission || rp.Permission == PermissionRegistry.Wildcard)
            .Select(rp => rp.RoleId);
        return db.Set<User>().Where(u => u.IsActive && u.Type == UserType.Staff && u.Roles.Any(r => r.Role!.IsActive && roleIds.Contains(r.RoleId)));
    }
}
