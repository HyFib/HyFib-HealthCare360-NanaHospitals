using HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>A bag of bio-medical waste: weighed sensibly, kept no more than 48 hours, handed over once, voided only before.</summary>
public sealed class BioMedicalWasteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "weightKg")]
    [InlineData(51, "weightKg")]
    public void A_bag_is_weighed_more_than_nothing_and_at_most_50_kg(decimal kg, string field) =>
        WasteBag.Validate(kg, Now, Now).Error!.Fields!.Keys.ShouldBe([field]);

    [Fact]
    public void A_bag_is_recorded_when_it_is_collected_not_in_the_future_or_a_week_later()
    {
        WasteBag.Validate(2.4m, Now, Now).IsSuccess.ShouldBeTrue();
        WasteBag.Validate(2.4m, Now.AddHours(1), Now).Error!.Fields!.Keys.ShouldBe(["collectedAt"]);
        WasteBag.Validate(2.4m, Now.AddDays(-8), Now).Error!.Fields!.Keys.ShouldBe(["collectedAt"]);
    }

    [Fact]
    public void A_bag_kept_beyond_48_hours_is_overdue_until_it_is_handed_over()
    {
        var bag = new WasteBag { Barcode = "NHBW26-0000001", CollectedAt = Now.AddHours(-49) };
        bag.IsOverdue(Now).ShouldBeTrue();
        new WasteBag { CollectedAt = Now.AddHours(-47) }.IsOverdue(Now).ShouldBeFalse();

        bag.HandOver(Guid.NewGuid(), 1m).IsSuccess.ShouldBeTrue();
        bag.IsOverdue(Now).ShouldBeFalse();
        bag.HandOver(Guid.NewGuid(), 1m).Error!.Code.ShouldBe("bmw.bag_not_pending");
        bag.Void("Wrong label").Error!.Code.ShouldBe("bmw.bag_not_pending");
    }

    [Fact]
    public void A_bag_is_voided_with_a_reason_and_then_never_handed_over()
    {
        var bag = new WasteBag { Barcode = "NHBW26-0000002", CollectedAt = Now };
        bag.Void(" ").Error!.Fields!.Keys.ShouldBe(["reason"]);
        bag.Void(" Weighed twice ").IsSuccess.ShouldBeTrue();
        (bag.Status, bag.VoidReason).ShouldBe((WasteBagStatus.Voided, "Weighed twice"));
        bag.HandOver(Guid.NewGuid(), 1m).Error!.Code.ShouldBe("bmw.bag_not_pending");
    }

    [Fact]
    public void A_bag_weighed_differently_at_the_handover_is_flagged_beyond_five_percent_and_at_least_100_grams()
    {
        WasteBag.Tolerance(1m).ShouldBe(0.1m);
        WasteBag.Tolerance(10m).ShouldBe(0.5m);

        var heavier = new WasteBag { Barcode = "BW1", WeightKg = 2.45m };
        heavier.HandOver(Guid.NewGuid(), 2.95m).IsSuccess.ShouldBeTrue();
        (heavier.HandoverWeightKg, heavier.WeightDifferenceKg, heavier.WeightMismatch).ShouldBe((2.95m, 0.5m, true));

        var same = new WasteBag { Barcode = "BW2", WeightKg = 2.45m };
        same.HandOver(Guid.NewGuid(), 2.5m).IsSuccess.ShouldBeTrue();
        same.WeightMismatch.ShouldBeFalse();

        new WasteBag { Barcode = "BW3", WeightKg = 1m }.HandOver(Guid.NewGuid(), 0m).Error!.Fields!.Keys.ShouldContain("bags");
    }

    [Theory]
    [InlineData(WasteCategory.Yellow, "Yellow bag", "Biohazard / Cytotoxic")]
    [InlineData(WasteCategory.Red, "Red bag", "Biohazard")]
    [InlineData(WasteCategory.White, "White translucent puncture-proof container", "Biohazard")]
    [InlineData(WasteCategory.Blue, "Cardboard box with blue marking", "Biohazard")]
    public void A_label_carries_the_colour_of_the_bag_and_its_symbol(WasteCategory category, string colour, string symbol)
    {
        WasteBag.Colour(category).ShouldBe(colour);
        WasteBag.Symbol(category).ShouldBe(symbol);
    }

    [Fact]
    public void Form_I_answers_every_item_from_the_record_and_leaves_what_is_not_kept_to_the_occupier()
    {
        var accident = new WasteAccident
        {
            OccurredAt = new DateTimeOffset(2026, 11, 8, 4, 30, 0, TimeSpan.Zero),
            Kind = WasteAccidentKind.NeedleStick,
            Sequence = "Recapping a used needle",
            Category = WasteCategory.White,
            WasteInvolved = "A used 24G needle",
            PersonsAffected = 1,
            Effects = "Possible exposure.",
            EmergencyMeasures = "Washed under running water",
            ReportedAt = new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero),
            ReportedByName = "Anita",
            ReportedByDesignation = "Staff nurse",
        };

        var lines = WasteForms.FormILines(accident, TimeSpan.FromHours(5.5));
        lines.Select(l => l.Number).Where(n => n.Length > 0).ShouldBe(["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"]);
        lines.Single(l => l.Number == "1").Answer.ShouldBe("08-11-2026 10:00");
        lines.Single(l => l.Number == "4").Answer.ShouldBe("No");
        lines.Single(l => l.Number == "5").Answer.ShouldBe("A used 24G needle (White category)");
        lines.Single(l => l.Number == "6").Answer.ShouldBe("Possible exposure. Persons affected: 1.");
        lines.Single(l => l.Number == "8").Answer.ShouldBe(WasteForms.ToFill);
        lines.Last().Answer.ShouldBe("Anita, Staff nurse");
    }

    [Fact]
    public void Form_IV_counts_the_years_waste_handovers_and_accidents()
    {
        var generated = Enum.GetValues<WasteCategory>().Select(c => new Features.Waste.WasteCategoryTotal(c, 2, 1.5m)).ToList();
        var accident = new WasteAccident { OccurredAt = new DateTimeOffset(2026, 3, 1, 4, 0, 0, TimeSpan.Zero), Kind = WasteAccidentKind.Spill, PersonsAffected = 2, EmergencyMeasures = "Spill kit used" };
        var lines = WasteForms.FormIvLines(2026, "Nana Hospitals", "Kumbakonam", "TN-BMW-1", 120, generated, 6m, 365, "Ramky CBWTF", 50, generated, 3, 1, [accident]);

        lines.Single(l => l.Number == "2(i)").Answer.ShouldBe("120");
        lines.Single(l => l.Number == "3").Answer.ShouldBe("6 kg in 2026 (0.016 kg per day on average)");
        lines.Single(l => l.Number == "4(ii)").Answer.ShouldBe("3");
        lines.Single(l => l.Number == "4(iii)").Answer.ShouldBe("1");
        lines.Single(l => l.Number == "7").Answer.ShouldBe("1 accidents, 2 persons affected");
        lines.Single(l => l.Number == "7(1)").Answer.ShouldBe("2 affected; remedial action: Spill kit used");
        lines.Single(l => l.Number == "6").Answer.ShouldBe(WasteForms.ToFill);
    }
}
