using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>
/// What an implant's bill says of it, and what it may be charged (NPPA, DPCO 2013): a consignment implant brings its
/// brand, maker and MRP from its pack; no unit is charged above the MRP on its pack, nor a price-controlled implant above
/// its NPPA ceiling price.
/// </summary>
public sealed class SurgeryMaterialPriceTests
{
    [Fact]
    public void A_consignment_implant_needs_its_brand_maker_and_the_mrp_on_its_pack()
    {
        SurgeryMaterial.ValidateConsignment(MaterialKind.Implant, " ", "Abbott Vascular", 38_933.14m).Error!.Fields!.ShouldContainKey("brand");
        SurgeryMaterial.ValidateConsignment(MaterialKind.Implant, "XIENCE Sierra", null, 38_933.14m).Error!.Fields!.ShouldContainKey("manufacturer");
        SurgeryMaterial.ValidateConsignment(MaterialKind.Implant, "XIENCE Sierra", "Abbott Vascular", 0m).Error!.Fields!.ShouldContainKey("mrp");
        SurgeryMaterial.ValidateConsignment(MaterialKind.Implant, "XIENCE Sierra", "Abbott Vascular", 38_933.14m).IsSuccess.ShouldBeTrue();
        SurgeryMaterial.ValidateConsignment(MaterialKind.Consumable, null, null, null).IsSuccess.ShouldBeTrue("A consumable brought by the patient needs none.");
    }

    [Fact]
    public void An_implant_from_the_ot_store_needs_no_lot_written_as_the_store_records_the_batch_it_issues()
    {
        SurgeryMaterial.Validate(MaterialKind.Implant, null, null, null, TestData.Today, fromStore: true).IsSuccess.ShouldBeTrue();
        SurgeryMaterial.Validate(MaterialKind.Implant, null, null, null, TestData.Today).Error!.Fields!.ShouldContainKey("lotNumber");
    }

    [Fact]
    public void A_unit_is_charged_at_most_the_lower_of_the_mrp_on_its_pack_and_its_ceiling()
    {
        // A pack printed at ₹60,000 before the ceiling of ₹57,456 was set.
        var knee = Material(unitPrice: 60_000m, mrp: 60_000m, ceiling: 57_456m);
        var refused = knee.CheckPrice().Error!;
        (refused.Fields!["unitPrice"].Single(), refused.Message).ShouldBe((
            "Knee system can be charged at most ₹57,456.00 a unit: its NPPA ceiling price.",
            "Knee system can be charged at most ₹57,456.00 a unit: its NPPA ceiling price."));
        knee.UnitPrice = 57_456m;
        knee.CheckPrice().IsSuccess.ShouldBeTrue();

        var stent = Material(unitPrice: 40_000m, mrp: 38_933.14m, ceiling: null);
        stent.CheckPrice().Error!.Message.ShouldBe("Knee system can be charged at most ₹38,933.14 a unit: the MRP printed on its pack.");
        stent.UnitPrice = 38_000m;
        stent.CheckPrice().IsSuccess.ShouldBeTrue("Less than the MRP may always be charged.");

        Material(unitPrice: 75_000m, mrp: null, ceiling: null).CheckPrice().IsSuccess.ShouldBeTrue("A consumable without an MRP has no limit to keep to.");
    }

    [Fact]
    public void Its_bill_details_are_the_brand_maker_specification_lot_serial_mrp_and_ceiling()
    {
        Material(unitPrice: 57_456m, mrp: 60_000m, ceiling: 57_456m).Device.ShouldBe(
            new DeviceDetails("Attune", "DePuy Synthes", "Primary total knee", "K1", "SN-1", 60_000m, 57_456m));
        new SurgeryMaterial { ItemName = "Gauze", Kind = MaterialKind.Consumable }.Device.ShouldBeNull("Nothing to print, nothing kept.");
    }

    private static SurgeryMaterial Material(decimal unitPrice, decimal? mrp, decimal? ceiling) => new()
    {
        Kind = MaterialKind.Implant,
        ItemName = "Knee system",
        Brand = "Attune",
        Manufacturer = "DePuy Synthes",
        Specification = "Primary total knee",
        LotNumber = "K1",
        SerialNumber = "SN-1",
        Quantity = 1m,
        UnitPrice = unitPrice,
        Mrp = mrp,
        CeilingPrice = ceiling,
    };
}
