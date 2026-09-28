using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class ClinicalModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new ClinicalModule().Name.ShouldBe("clinical");

    [Fact]
    public void Module_declares_its_permissions() => new ClinicalModule().Permissions.ShouldBe(ClinicalPermissions.All);

    [Fact]
    public void Permission_names_are_unique_and_prefixed()
    {
        var names = ClinicalPermissions.All.Select(p => p.Name).ToList();

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Count);
        names.ShouldAllBe(n => n.StartsWith("clinical.", StringComparison.Ordinal) || n.StartsWith("mrd.", StringComparison.Ordinal)
                               || n.StartsWith("healthcheck.", StringComparison.Ordinal));
        names.ShouldContain(ClinicalPermissions.EmrRead);
        names.ShouldContain(ClinicalPermissions.PrescriptionsSign);
        names.ShouldContain(ClinicalPermissions.MrdCoding);
        names.ShouldContain(ClinicalPermissions.HealthCheckManage);
    }

    [Fact]
    public void Every_permission_has_default_roles_and_a_description() =>
        ClinicalPermissions.All.ShouldAllBe(p => p.DefaultRoles != null && p.DefaultRoles.Length > 0 && p.Description.Length > 0);

    [Fact]
    public void Nurses_can_read_record_vitals_and_allergies_but_not_prescribe()
    {
        string[] nurse = [.. ClinicalPermissions.All.Where(p => p.DefaultRoles!.Contains(SystemRoles.Nurse)).Select(p => p.Name)];

        nurse.ShouldContain(ClinicalPermissions.EmrRead);
        nurse.ShouldContain(ClinicalPermissions.VitalsRecord);
        nurse.ShouldContain(ClinicalPermissions.AllergiesManage);
        nurse.ShouldNotContain(ClinicalPermissions.PrescriptionsSign);
        nurse.ShouldNotContain(ClinicalPermissions.OrdersPlace);
    }

    [Fact]
    public void Coding_belongs_to_the_mrd_officer() =>
        ClinicalPermissions.All.Single(p => p.Name == ClinicalPermissions.MrdCoding).DefaultRoles!.ShouldBe([SystemRoles.MrdOfficer]);
}
