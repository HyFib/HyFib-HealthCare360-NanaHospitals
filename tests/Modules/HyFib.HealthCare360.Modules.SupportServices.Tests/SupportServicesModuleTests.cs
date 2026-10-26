namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

public sealed class SupportServicesModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new SupportServicesModule().Name.ShouldBe("supportservices");

    [Fact]
    public void Permissions_are_unique_and_use_the_module_prefixes()
    {
        var names = new SupportServicesModule().Permissions.Select(p => p.Name).ToList();

        names.ShouldBeUnique();
        names.ShouldAllBe(n => n.StartsWith("cssd.") || n.StartsWith("dietary.") || n.StartsWith("canteen.") || n.StartsWith("bmw.") || n.StartsWith("mortuary.") || n.StartsWith("ambulance.") || n.StartsWith("linen."));
    }

    [Fact]
    public void Every_permission_is_granted_to_at_least_one_role_by_default() =>
        new SupportServicesModule().Permissions.ShouldAllBe(p => p.DefaultRoles != null && p.DefaultRoles.Length > 0);
}
