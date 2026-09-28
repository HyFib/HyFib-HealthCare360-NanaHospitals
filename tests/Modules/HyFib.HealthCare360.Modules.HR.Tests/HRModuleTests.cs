using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class HRModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new HRModule().Name.ShouldBe("hr");

    [Fact]
    public void Declares_every_hr_permission_once_under_the_hr_prefix()
    {
        var permissions = new HRModule().Permissions.ToList();

        permissions.Select(p => p.Name).ShouldBe(
        [
            HRPermissions.EmployeesRead, HRPermissions.EmployeesManage, HRPermissions.AttendanceManage, HRPermissions.RosterManage,
            HRPermissions.LeaveApprove, HRPermissions.PayrollProcess, HRPermissions.PayrollApprove, HRPermissions.CredentialsManage, HRPermissions.Self,
        ]);
        permissions.ShouldAllBe(p => p.Name.StartsWith("hr.", StringComparison.Ordinal));
    }

    [Fact]
    public void Hr_work_defaults_to_hr_managers_and_admins_while_self_service_is_for_all_staff()
    {
        var permissions = new HRModule().Permissions.ToDictionary(p => p.Name);

        permissions[HRPermissions.PayrollApprove].DefaultRoles.ShouldBe([SystemRoles.HrManager, SystemRoles.HospitalAdmin]);
        permissions[HRPermissions.Self].DefaultRoles!.ShouldContain(SystemRoles.Nurse);
        permissions[HRPermissions.Self].DefaultRoles!.ShouldNotContain(SystemRoles.Patient);
    }
}
