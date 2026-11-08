namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>
/// A scheduled medicine's NPPA ceiling price (DPCO 2013): no pack received with an MRP above it, none sold above it; and
/// a ceiling raised or removed, or a GST rate changed, waits for a second person while a ceiling set or lowered does not.
/// </summary>
public sealed class PriceCeilingTests
{
    /// <summary>Metformin 500 mg, counted in tablets and bought in strips of 10, ceiling ₹2.3456 a tablet with GST.</summary>
    private static Item Metformin(decimal? ceiling = 2.3456m) => new()
    {
        Code = "MET500",
        Name = "Metformin 500 mg Tablet",
        Type = ItemType.Drug,
        BaseUnit = "TAB",
        PurchaseUnit = "STRIP",
        PurchaseUnitFactor = 10,
        GstRate = 5,
        Schedule = DrugSchedule.H,
        CeilingPrice = ceiling,
    };

    [Theory]
    [InlineData(23.45, false)]
    [InlineData(23.46, false)]
    [InlineData(23.47, true)]
    [InlineData(30, true)]
    public void A_pack_is_above_the_ceiling_only_beyond_its_units_ceiling_to_the_paisa(double packMrp, bool above) =>
        PriceCeiling.PackExceeds((decimal)packMrp, 2.3456m, 10).ShouldBe(above, "₹2.3456 × 10 = ₹23.456, ₹23.46 to the paisa");

    [Fact]
    public void A_batch_received_at_the_ceiling_is_sold_at_it_though_its_unit_mrp_is_rounded()
    {
        // ₹23.46 a strip is ₹2.346 a tablet, above ₹2.3456 by its fourth place, and still the ceiling as printed.
        PriceCeiling.UnitExceeds(2.346m, 2.3456m, 10).ShouldBeFalse();
        PriceCeiling.UnitExceeds(2.347m, 2.3456m, 10).ShouldBeTrue();

        // A strip of 3 at ₹10.00 is ₹3.3333 a tablet: × 3 is ₹9.9999, ₹10.00 to the paisa.
        PriceCeiling.UnitExceeds(3.3333m, 3.3334m, 3).ShouldBeFalse();
    }

    [Fact]
    public void Only_medicines_are_refused_above_the_ceiling_an_implant_is_charged_at_it()
    {
        PriceCeiling.AppliesTo(ItemType.Drug, 2m).ShouldBeTrue();
        PriceCeiling.AppliesTo(ItemType.Implant, 30000m).ShouldBeFalse("an implant is charged at the lower of MRP and ceiling");
        PriceCeiling.AppliesTo(ItemType.Drug, null).ShouldBeFalse();
    }

    [Fact]
    public void A_goods_receipt_line_above_the_ceiling_is_refused_with_its_code()
    {
        var line = new GoodsReceiptLine
        {
            ItemName = "Metformin 500 mg Tablet",
            BatchNumber = "M1",
            ExpiryDate = new DateOnly(2030, 1, 1),
            Quantity = 10,
            Rate = 15,
            Mrp = 25,
            UnitsPerPurchaseUnit = 10,
            RequiresBatch = true,
            RequiresMrp = true,
            CeilingPrice = 2.3456m,
        };
        line.Validate(new DateOnly(2026, 11, 8)).Error.ShouldNotBeNull().Code.ShouldBe(PriceCeiling.ReceiptCode);

        line.Mrp = 23.46m;
        line.Validate(new DateOnly(2026, 11, 8)).IsSuccess.ShouldBeTrue();

        // A ceiling lowered after the receipt was entered refuses it when it is posted.
        line.CheckCeiling(2m).Error.ShouldNotBeNull().Code.ShouldBe(PriceCeiling.ReceiptCode);
    }

    [Fact]
    public void A_medicine_takes_a_ceiling_and_a_consumable_does_not()
    {
        Metformin().Validate().IsSuccess.ShouldBeTrue();
        var gauze = Metformin();
        gauze.Type = ItemType.Consumable;
        gauze.Schedule = DrugSchedule.None;
        gauze.Validate().Error.ShouldNotBeNull().Fields!.ShouldContainKey("ceilingPrice");
    }

    [Theory]
    [InlineData(2.3456, 2.0, 5, 5, false)]   // lowered: at once
    [InlineData(null, 2.0, 5, 5, false)]     // set where there was none: at once
    [InlineData(2.3456, 2.5, 5, 5, true)]    // raised: waits
    [InlineData(2.3456, null, 5, 5, true)]   // removed: waits
    [InlineData(2.3456, 2.3456, 5, 12, true)] // another GST rate: waits
    [InlineData(2.3456, 2.3456, 12, 5, true)]
    [InlineData(null, null, 5, 5, false)]
    public void Loosening_the_price_control_waits_and_tightening_applies_at_once(double? before, double? after, double gstBefore, double gstAfter, bool waits) =>
        new ItemPricing((decimal?)before, null, (decimal)gstBefore).IsLoosenedBy(new ItemPricing((decimal?)after, null, (decimal)gstAfter)).ShouldBe(waits);

    [Fact]
    public void The_pricing_reads_in_words_per_unit_and_per_pack()
    {
        var item = Metformin();
        new ItemPricing(2.3456m, "S.O. 1234(E) of 30-Mar-2026", 5).Words(item)
            .ShouldBe("Ceiling ₹2.3456 a TAB (₹23.46 a STRIP of 10) with GST, S.O. 1234(E) of 30-Mar-2026; GST 5%");
        new ItemPricing(null, null, 12).Words(item).ShouldBe("No ceiling price; GST 12%");
    }
}
