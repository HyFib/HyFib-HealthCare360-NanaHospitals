using HyFib.HealthCare360.Modules.SpecialtyCare.Services;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>How a correction of an implant or consumable line is put in words for the OT in-charge (maker-checker slice 10).</summary>
public sealed class MaterialFactsTests
{
    private static readonly MaterialFacts Recorded = new("A12", null, new DateOnly(2027, 3, 31), 2m);

    [Fact]
    public void A_correction_names_each_thing_that_changes() =>
        MaterialFacts.Change(Recorded, Recorded with { LotNumber = "A21", Quantity = 1m }).ShouldBe("lot A12 → A21; quantity 2 → 1");

    [Fact]
    public void A_serial_and_expiry_are_named_too() =>
        MaterialFacts.Change(Recorded, Recorded with { SerialNumber = "SN9", ExpiryDate = new DateOnly(2027, 4, 30) })
            .ShouldBe("serial none → SN9; expiry 31 Mar 2027 → 30 Apr 2027");

    [Fact]
    public void A_quantity_of_nothing_says_the_item_was_not_used() =>
        MaterialFacts.Change(Recorded, Recorded with { Quantity = 0m }).ShouldBe("not used (quantity 2 → 0)");
}
