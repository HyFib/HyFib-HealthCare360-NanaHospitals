using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A device on a bill, as NPPA asks a hospital to bill a coronary stent or a knee implant: the bill line keeps its brand,
/// maker, batch, specification and MRP, and a unit is never charged above the lower of the MRP on its pack and its NPPA
/// ceiling price.
/// </summary>
public sealed class DeviceBillTests
{
    [Fact]
    public void A_device_is_charged_at_most_the_lower_of_its_mrp_and_its_ceiling()
    {
        // A pack printed before the ceiling was cut: the ceiling limits.
        var oldPack = Device(mrp: 60_000m, ceiling: 57_456m);
        (oldPack.PriceLimit(), oldPack.CeilingLimits()).ShouldBe((57_456m, true));

        // A pack priced below the ceiling: its MRP limits.
        var newPack = Device(mrp: 50_000m, ceiling: 57_456m);
        (newPack.PriceLimit(), newPack.CeilingLimits()).ShouldBe((50_000m, false));

        (Device(mrp: 38_933.14m, ceiling: null).PriceLimit(), Device(mrp: null, ceiling: 57_456m).PriceLimit()).ShouldBe((38_933.14m, 57_456m));
        Device(mrp: 0m, ceiling: null).PriceLimit().ShouldBeNull("A pack without an MRP sets no limit of its own.");
        Device(mrp: null, ceiling: null).PriceLimit().ShouldBeNull();
    }

    [Fact]
    public void Details_are_kept_trimmed_and_none_are_kept_when_there_is_nothing_to_print()
    {
        DeviceDetails.Of(" Attune ", null, null, " K1 ", "", 0m, null).ShouldBe(new DeviceDetails("Attune", null, null, "K1", null, null, null));
        DeviceDetails.Of(" ", null, "", null, null, 0m, -1m).ShouldBeNull();
    }

    [Fact]
    public void The_bill_line_keeps_the_device_of_its_charge()
    {
        var device = Device(mrp: 60_000m, ceiling: 57_456m);
        var charge = new Charge
        {
            ServiceCode = "OT-IMPLANT",
            Description = "Implant: knee system",
            Quantity = 1m,
            UnitPrice = 57_456m,
            GrossAmount = 57_456m,
            NetAmount = 57_456m,
            Device = device,
        };

        BillLine.From(Guid.CreateVersion7(), charge, true, null, 1).Device.ShouldBe(device);
    }

    [Fact]
    public void A_device_charged_above_its_limit_is_refused_naming_the_limit_that_applies()
    {
        var aboveCeiling = ChargeService.DevicePriceError(Device(mrp: 60_000m, ceiling: 57_456m), "Knee system", 58_000m, 57_456m);
        (aboveCeiling.Code, aboveCeiling.Message).ShouldBe(("billing.device_above_ceiling", "Attune is charged ₹58,000.00 a unit, above its NPPA ceiling price of ₹57,456.00."));

        var aboveMrp = ChargeService.DevicePriceError(Device(mrp: 38_933.14m, ceiling: null) with { Brand = null }, "Coronary stent", 40_000m, 38_933.14m);
        (aboveMrp.Code, aboveMrp.Message).ShouldBe(("billing.device_above_mrp", "Coronary stent is charged ₹40,000.00 a unit, above the MRP of ₹38,933.14 printed on its pack."));
    }

    private static DeviceDetails Device(decimal? mrp, decimal? ceiling) =>
        new("Attune", "DePuy Synthes", "Primary total knee, cobalt-chromium", "K1", "SN-1", mrp, ceiling);
}
