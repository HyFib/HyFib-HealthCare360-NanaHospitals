using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>Identification-only facts about an employee, safe to hand to any module.</summary>
/// <param name="IsActive">On the rolls today and not suspended (see <see cref="EmployeeDirectory.IsActiveOn"/>).</param>
public sealed record EmployeeDirectoryEntry(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    Guid FacilityId,
    Guid? DepartmentId,
    string? DepartmentName,
    string? DesignationName,
    bool IsActive);

/// <summary>
/// Read-only employee lookup for other modules, for example the canteen validating a staff code before charging staff
/// credit (CAN-03) and then posting it with <see cref="PayrollInputService.PostDeductionAsync"/>. It returns
/// identification only — never bank, PAN, statutory, contact or salary data — so callers need no HR permission.
/// </summary>
public interface IEmployeeDirectory
{
    /// <summary>The employee with this code (case-insensitive, any facility of the group), or null.</summary>
    Task<EmployeeDirectoryEntry?> FindByCodeAsync(string code, CancellationToken ct);

    /// <summary>The employee with this id (any facility), or null; lets a caller re-check a stored id before posting.</summary>
    Task<EmployeeDirectoryEntry?> FindByIdAsync(Guid employeeId, CancellationToken ct);
}

/// <summary>
/// Employee codes carry the facility prefix and are unique across the group, and payroll deductions post to the
/// employee's own facility, so the lookup ignores the caller's facility and returns <see cref="EmployeeDirectoryEntry.FacilityId"/>
/// for callers that must restrict to their own site. Soft-deleted records are never returned.
/// </summary>
internal sealed class EmployeeDirectory(HospitalDbContext db, TimeProvider time) : IEmployeeDirectory
{
    private const int MaxCodeLength = 24;

    public async Task<EmployeeDirectoryEntry?> FindByCodeAsync(string code, CancellationToken ct)
    {
        var normalized = NormalizeCode(code);
        return normalized is null ? null : await FindAsync(e => e.EmployeeCode == normalized, ct);
    }

    public Task<EmployeeDirectoryEntry?> FindByIdAsync(Guid employeeId, CancellationToken ct) =>
        employeeId == Guid.Empty ? Task.FromResult<EmployeeDirectoryEntry?>(null) : FindAsync(e => e.Id == employeeId, ct);

    /// <summary>Upper-case letters and digits only (codes look like NHEMP00042); anything else cannot be a code.</summary>
    public static string? NormalizeCode(string? code)
    {
        var trimmed = code?.Trim().ToUpperInvariant();
        return trimmed is { Length: > 0 and <= MaxCodeLength } && trimmed.All(char.IsAsciiLetterOrDigit) ? trimmed : null;
    }

    /// <summary>
    /// Active means employed on <paramref name="today"/> (joined, not past the last working day) in a working status.
    /// Suspended and separated employees are inactive, so nothing new (such as canteen credit) is charged to them.
    /// </summary>
    public static bool IsActiveOn(EmployeeStatus status, DateOnly joiningDate, DateOnly? leavingDate, DateOnly today) =>
        status is EmployeeStatus.Active or EmployeeStatus.Probation or EmployeeStatus.OnNotice
        && joiningDate <= today
        && (leavingDate is null || today <= leavingDate);

    private async Task<EmployeeDirectoryEntry?> FindAsync(Expression<Func<Employee, bool>> predicate, CancellationToken ct)
    {
        var row = await (
                from e in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Where(predicate)
                join d in db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on e.DepartmentId equals d.Id into departments
                from d in departments.DefaultIfEmpty()
                join g in db.Set<Designation>() on e.DesignationId equals g.Id into designations
                from g in designations.DefaultIfEmpty()
                select new
                {
                    e.Id,
                    e.EmployeeCode,
                    e.FullName,
                    e.FacilityId,
                    e.DepartmentId,
                    DepartmentName = d != null ? d.Name : null,
                    DesignationName = g != null ? g.Name : null,
                    e.Status,
                    e.JoiningDate,
                    e.LeavingDate,
                })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? null
            : new EmployeeDirectoryEntry(row.Id, row.EmployeeCode, row.FullName, row.FacilityId, row.DepartmentId, row.DepartmentName,
                row.DesignationName, IsActiveOn(row.Status, row.JoiningDate, row.LeavingDate, time.LocalToday()));
    }
}
