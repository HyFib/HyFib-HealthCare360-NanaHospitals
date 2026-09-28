namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>
/// Guards the reporting hierarchy. Leave and regularisation approvals walk up this chain, so a loop
/// (A reports to B, B reports to A) would leave requests with no one able to approve them.
/// </summary>
public static class ReportingChain
{
    /// <summary>Longer chains than this are treated as corrupt rather than walked forever.</summary>
    public const int MaxDepth = 25;

    /// <summary>
    /// True when making <paramref name="managerId"/> the manager of <paramref name="employeeId"/> would create a loop
    /// (or an implausibly deep chain). <paramref name="managerOf"/> returns the current manager of an employee.
    /// </summary>
    public static bool CreatesCycle(Guid employeeId, Guid? managerId, Func<Guid, Guid?> managerOf)
    {
        var current = managerId;
        for (var depth = 0; current is { } id; depth++)
        {
            if (id == employeeId || depth >= MaxDepth)
            {
                return true;
            }

            current = managerOf(id);
        }

        return false;
    }
}
