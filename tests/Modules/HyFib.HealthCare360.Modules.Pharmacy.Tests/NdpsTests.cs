using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>Which drugs are the NDPS essential narcotic drugs, next year's estimate, and the two-person count at a change of shift.</summary>
public sealed class NdpsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Morphine sulphate", true)]
    [InlineData("fentanyl citrate", true)]
    [InlineData("Codeine phosphate", true)]
    [InlineData("Tramadol", false)]
    [InlineData("Buprenorphine", false)]
    [InlineData(null, false)]
    public void An_essential_narcotic_drug_is_known_by_its_generic_name(string? generic, bool end) =>
        EssentialNarcoticDrugs.Is(generic).ShouldBe(end);

    [Fact]
    public void Next_years_estimate_is_this_years_consumption_with_a_margin_in_whole_units()
    {
        EssentialNarcoticDrugs.Estimate(120).ShouldBe(132);
        EssentialNarcoticDrugs.Estimate(7).ShouldBe(8);
        EssentialNarcoticDrugs.Estimate(0).ShouldBe(0);
    }

    [Fact]
    public void A_count_is_witnessed_by_someone_else_and_a_difference_is_explained_first()
    {
        var counter = Guid.NewGuid();
        var count = new NarcoticCount
        {
            CountedBy = counter,
            Lines = [new NarcoticCountLine { ItemName = "Morphine 10 mg/ml Injection", RegisterBalance = 20, Counted = 19 }],
        };
        count.HasDifference.ShouldBeTrue();
        count.Lines[0].Difference.ShouldBe(-1);

        count.Witness(counter, "Same nurse", "Broken ampoule", Now).Error!.Code.ShouldBe("ndps.witness_self");
        count.Witness(Guid.NewGuid(), "Sr. Kavitha", null, Now).Error!.Fields!.Keys.ShouldBe(["explanation"]);
        count.Witness(Guid.NewGuid(), "Sr. Kavitha", " One ampoule broken at 06:10, wastage witnessed ", Now).IsSuccess.ShouldBeTrue();
        (count.Status, count.Explanation, count.WitnessedByName).ShouldBe(
            (NarcoticCountStatus.Witnessed, "One ampoule broken at 06:10, wastage witnessed", "Sr. Kavitha"));
        count.Witness(Guid.NewGuid(), "Another", null, Now).Error!.Code.ShouldBe("ndps.count_witnessed");
    }

    [Fact]
    public void A_count_that_agrees_with_the_register_needs_no_explanation()
    {
        var count = new NarcoticCount
        {
            CountedBy = Guid.NewGuid(),
            Lines = [new NarcoticCountLine { RegisterBalance = 5, Counted = 5 }],
        };
        count.Witness(Guid.NewGuid(), "Pharmacist", null, Now).IsSuccess.ShouldBeTrue();
    }
}
