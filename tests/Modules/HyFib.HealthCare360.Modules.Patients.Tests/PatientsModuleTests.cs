namespace HyFib.HealthCare360.Modules.Patients.Tests;

public sealed class PatientsModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new PatientsModule().Name.ShouldBe("patients");
}
