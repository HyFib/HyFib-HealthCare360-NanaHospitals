using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Who may decide an employee's requests, and who hears about them. The reporting manager (through their linked
/// user account) decides first-line; holders of the HR permission can always decide. Requests of employees without
/// a manager with a user account go to HR.
/// </summary>
public sealed class HrApprovers(HospitalDbContext db, ICurrentUser currentUser, IPermissionService permissions, INotificationService notifications)
{
    /// <summary>
    /// Succeeds when the current user may decide requests of <paramref name="employeeId"/> — either as holder of
    /// <paramref name="hrPermission"/> working in the employee's facility, or as the employee's reporting manager
    /// (managers may sit in another branch).
    /// </summary>
    public async Task<Result<Guid>> EnsureCanDecideAsync(Guid employeeId, Guid employeeFacilityId, string hrPermission, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "Sign in first.");
        }

        if (currentUser.FacilityId == employeeFacilityId && await permissions.HasPermissionAsync(userId, hrPermission, ct))
        {
            return userId;
        }

        var managerUserId = await ManagerUserIdAsync(employeeId, ct);
        return managerUserId == userId
            ? userId
            : Error.Forbidden("hr.not_approver", "Only the employee's reporting manager or HR can decide this request.");
    }

    /// <summary>User account of the employee's reporting manager, if any.</summary>
    public Task<Guid?> ManagerUserIdAsync(Guid employeeId, CancellationToken ct) =>
        (from e in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
         join m in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on e.ReportingManagerId equals m.Id
         where e.Id == employeeId && m.Status != EmployeeStatus.Separated
         select m.UserId).FirstOrDefaultAsync(ct);

    /// <summary>Tells the manager (or, failing that, HR) that a request is waiting.</summary>
    public async Task NotifyRequestAsync(Guid employeeId, Guid facilityId, string hrPermission, string title, string body, string link, CancellationToken ct)
    {
        var managerUserId = await ManagerUserIdAsync(employeeId, ct);
        if (managerUserId is { } manager && manager != currentUser.UserId)
        {
            await notifications.NotifyUsersAsync([manager], title, body, link, "hr", "info", ct);
            return;
        }

        await notifications.NotifyPermissionHoldersAsync(hrPermission, facilityId, title, body, link, "hr", "info", ct);
    }

    /// <summary>Tells the employee (through their user account) about a decision.</summary>
    public async Task NotifyEmployeeAsync(Guid employeeId, string title, string body, string link, CancellationToken ct)
    {
        var userId = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.Id == employeeId)
            .Select(e => e.UserId)
            .FirstOrDefaultAsync(ct);
        if (userId is { } id && id != currentUser.UserId)
        {
            await notifications.NotifyUsersAsync([id], title, body, link, "hr", "info", ct);
        }
    }
}
