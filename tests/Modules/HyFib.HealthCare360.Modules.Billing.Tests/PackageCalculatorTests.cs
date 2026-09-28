using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class PackageCalculatorTests
{
    private static readonly Guid BedGeneral = Guid.CreateVersion7();
    private static readonly Guid Cbc = Guid.CreateVersion7();
    private static readonly Guid LiverPanel = Guid.CreateVersion7();
    private static readonly Guid Implant = Guid.CreateVersion7();

    private static PackageUsage Use(Guid item, ServiceCategory category, decimal quantity, decimal unitPrice, int day = 0) =>
        new(Guid.CreateVersion7(), item, category, quantity, unitPrice, Money.Round(quantity * unitPrice), TestData.Now.AddDays(day));

    [Fact]
    public void Quantity_cap_absorbs_the_included_days_and_the_rest_is_excess()
    {
        var bed = Use(BedGeneral, ServiceCategory.BedCharge, 3m, 1_500m);

        var outcome = PackageCalculator.Allocate(35_000m, [new PackageRule(BedGeneral, null, 2m, null)], [bed]);

        outcome.Lines[0].Covered.ShouldBe(3_000m);
        outcome.Lines[0].Excess.ShouldBe(1_500m);
    }

    [Fact]
    public void Category_amount_cap_is_consumed_earliest_service_first()
    {
        var early = Use(Cbc, ServiceCategory.Laboratory, 1m, 2_000m, day: 0);
        var late = Use(LiverPanel, ServiceCategory.Laboratory, 1m, 1_500m, day: 1);

        var outcome = PackageCalculator.Allocate(35_000m, [new PackageRule(null, ServiceCategory.Laboratory, null, 3_000m)], [late, early]);

        outcome.Lines.Single(l => l.Key == early.Key).Covered.ShouldBe(2_000m);
        var lateLine = outcome.Lines.Single(l => l.Key == late.Key);
        lateLine.Covered.ShouldBe(1_000m);
        lateLine.Excess.ShouldBe(500m);
    }

    [Fact]
    public void Services_the_package_does_not_include_are_billed_in_full()
    {
        var implant = Use(Implant, ServiceCategory.Consumable, 1m, 45_000m);

        var outcome = PackageCalculator.Allocate(35_000m, [new PackageRule(null, ServiceCategory.Laboratory, null, 3_000m)], [implant]);

        outcome.Lines[0].Covered.ShouldBe(0m);
        outcome.Lines[0].Excess.ShouldBe(45_000m);
        outcome.Lines[0].RuleIndex.ShouldBeNull();
    }

    [Fact]
    public void An_item_specific_inclusion_governs_its_item_exclusively()
    {
        var firstCbc = Use(Cbc, ServiceCategory.Laboratory, 1m, 300m, day: 0);
        var secondCbc = Use(Cbc, ServiceCategory.Laboratory, 1m, 300m, day: 1);
        var liver = Use(LiverPanel, ServiceCategory.Laboratory, 1m, 800m, day: 0);
        PackageRule[] rules = [new PackageRule(Cbc, null, 1m, null), new PackageRule(null, ServiceCategory.Laboratory, null, 5_000m)];

        var outcome = PackageCalculator.Allocate(10_000m, rules, [firstCbc, secondCbc, liver]);

        outcome.Lines.Single(l => l.Key == firstCbc.Key).Covered.ShouldBe(300m);
        outcome.Lines.Single(l => l.Key == secondCbc.Key).Excess.ShouldBe(300m);
        outcome.Lines.Single(l => l.Key == liver.Key).Covered.ShouldBe(800m);
    }

    [Fact]
    public void Quantity_and_amount_caps_apply_together()
    {
        var first = Use(Implant, ServiceCategory.Consumable, 2m, 3_000m, day: 0);
        var second = Use(Implant, ServiceCategory.Consumable, 1m, 3_000m, day: 1);

        var outcome = PackageCalculator.Allocate(20_000m, [new PackageRule(Implant, null, 3m, 5_000m)], [first, second]);

        var firstLine = outcome.Lines.Single(l => l.Key == first.Key);
        firstLine.Covered.ShouldBe(5_000m);
        firstLine.Excess.ShouldBe(1_000m);
        outcome.Lines.Single(l => l.Key == second.Key).Excess.ShouldBe(3_000m);
    }

    [Fact]
    public void Variance_shows_the_hospitals_gain_or_loss_against_actuals()
    {
        var bed = Use(BedGeneral, ServiceCategory.BedCharge, 2m, 1_500m);
        var implant = Use(Implant, ServiceCategory.Consumable, 1m, 2_000m);

        var outcome = PackageCalculator.Allocate(35_000m, [new PackageRule(BedGeneral, null, 2m, null)], [bed, implant]);

        outcome.Actuals.ShouldBe(5_000m);
        outcome.Covered.ShouldBe(3_000m);
        outcome.Excess.ShouldBe(2_000m);
        outcome.Variance.ShouldBe(32_000m);
    }
}
