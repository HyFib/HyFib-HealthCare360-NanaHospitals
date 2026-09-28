using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Resolves the employee record linked to the signed-in user for self-service. The lookup ignores the facility
/// filter because a user's own record belongs to them whichever branch they are working in today.
/// </summary>
public sealed class CurrentEmployee(HospitalDbContext db, ICurrentUser currentUser)
{
    private Guid? _employeeId;

    public async Task<Result<Guid>> IdAsync(CancellationToken ct)
    {
        if (_employeeId is { } cached)
        {
            return cached;
        }

        if (currentUser.UserId is not { } userId || currentUser.IsPatient)
        {
            return Error.Forbidden("hr.self.no_user", "Sign in with a staff account.");
        }

        var id = await db.Set<Employee>()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.UserId == userId && e.Status != EmployeeStatus.Separated)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);

        if (id is null)
        {
            return Error.Forbidden("hr.self.not_linked", "Your user account is not linked to an employee record. Contact HR.");
        }

        _employeeId = id;
        return id.Value;
    }
}
