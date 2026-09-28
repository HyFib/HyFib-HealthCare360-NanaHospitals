namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

public sealed class SupplyChainModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new SupplyChainModule().Name.ShouldBe("supplychain");
}
