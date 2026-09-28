namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

public sealed class PharmacyModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new PharmacyModule().Name.ShouldBe("pharmacy");
}
