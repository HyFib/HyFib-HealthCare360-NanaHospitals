using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Employees' codes and names by id, from every facility and removed ones too, read in a query of their own. For a list
/// that names who asked: an employee who moved keeps the requests the facility they worked at holds. EF Core ignores a query
/// filter for the whole query, so reading the employees inside the list's query would take the facility filter off the
/// list too, and HR would list every facility's requests.
/// </summary>
internal static class EmployeeNames
{
    public static async Task<IReadOnlyDictionary<Guid, (string Code, string Name)>> ByIdAsync(
        HospitalDbContext db, IEnumerable<Guid> employeeIds, CancellationToken ct)
    {
        var ids = employeeIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, (string Code, string Name)>();
        }

        var employees = await db.Set<Employee>().IgnoreQueryFilters().AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.EmployeeCode, e.FullName })
            .ToListAsync(ct);
        return employees.ToDictionary(e => e.Id, e => (e.EmployeeCode, e.FullName));
    }
}
