namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class InpatientModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new InpatientModule().Name.ShouldBe("inpatient");

    [Fact]
    public void Permissions_are_unique_and_use_the_module_prefixes()
    {
        var names = new InpatientModule().Permissions.Select(p => p.Name).ToList();

        names.ShouldBeUnique();
        names.ShouldAllBe(n => n.StartsWith("inpatient.") || n.StartsWith("beds.") || n.StartsWith("nursing."));
        names.ShouldContain(InpatientPermissions.Admit);
        names.ShouldContain(InpatientPermissions.NursingMar);
    }

    [Fact]
    public void Every_permission_is_granted_to_at_least_one_role_by_default() =>
        new InpatientModule().Permissions.ShouldAllBe(p => p.DefaultRoles != null && p.DefaultRoles.Length > 0);
}
