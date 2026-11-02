using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A tariff plan's prices and terms change only when two people agree: the rule's parts, row by row. The terms compare by
/// what they decide (the multipliers in any order); a new plan waits out of use with nothing that prices anyone; the
/// approver reads every change in words; and an approval is made only over the very rows it replaces. The integration
/// tests drive them over the API; the rows they leave out are here.
/// </summary>
public sealed class TariffPlanTermsTests
{
    private static readonly Guid PrivateRoom = Guid.Parse("0b000000-0000-0000-0000-000000000001");
    private static readonly Guid Icu = Guid.Parse("0b000000-0000-0000-0000-000000000002");
    private static readonly Guid Facility = Guid.Parse("0f000000-0000-0000-0000-000000000001");

    private static readonly TariffPlanTerms InUse = TariffPlanTerms.Create(
        TariffType.Corporate, null, 10m, isDefault: false, isActive: true, [new(PrivateRoom, 1.5m), new(Icu, 2.5m)]);

    [Fact]
    public void Terms_compare_by_what_they_decide()
    {
        TariffPlanTerms.Create(TariffType.Corporate, null, 10m, false, true, [new(Icu, 2.5m), new(PrivateRoom, 1.5m)])
            .ShouldBe(InUse, "the same multipliers in another order are no change");
        (InUse with { DefaultDiscountPercent = 12m }).ShouldNotBe(InUse);
        TariffPlanTerms.Create(TariffType.Corporate, null, 10m, false, true, [new(PrivateRoom, 1.5m), new(Icu, 3m)]).ShouldNotBe(InUse);
        TariffPlanTerms.Create(TariffType.Corporate, null, 10m, false, true, [new(PrivateRoom, 1.5m)]).ShouldNotBe(InUse);
        (InUse with { FacilityId = Facility }).ShouldNotBe(InUse);
        (InUse with { Type = TariffType.Insurance }).ShouldNotBe(InUse);
        TariffPlanTerms.Create(TariffType.Corporate, null, 10m, isDefault: true, isActive: false, null).IsDefault
            .ShouldBeFalse("a plan out of use is never the default, as the plan itself keeps it");
    }

    [Fact]
    public void A_new_plan_waits_out_of_use_with_nothing_that_prices_anyone()
    {
        var asked = TariffPlanTerms.Create(TariffType.Corporate, Facility, 10m, isDefault: true, isActive: true, [new(PrivateRoom, 1.5m)]);
        var now = asked.OutOfUse();

        (now.IsActive, now.IsDefault, now.DefaultDiscountPercent, now.BedMultipliers.Count).ShouldBe((false, false, 0m, 0));
        (now.Type, now.FacilityId).ShouldBe((TariffType.Corporate, Facility), "they price nobody while it is out of use");
        now.ShouldNotBe(asked);
        TariffPlanTerms.Create(TariffType.Corporate, null, 0m, false, false, null).OutOfUse()
            .ShouldBe(TariffPlanTerms.Create(TariffType.Corporate, null, 0m, false, false, null), "nothing to ask for");
    }

    [Fact]
    public void The_approver_reads_every_change_in_words()
    {
        var beds = new Dictionary<Guid, string> { [PrivateRoom] = "Private room", [Icu] = "ICU bed" };
        var asked = TariffPlanTerms.Create(TariffType.Insurance, Facility, 15m, isDefault: true, isActive: true, [new(PrivateRoom, 2m)]);

        TariffApprovals.Changes(InUse.OutOfUse(), asked, beds).Select(c => c.Words).ShouldBe(
        [
            "put in use",
            "the default Insurance plan",
            "type Corporate → Insurance",
            "for one facility",
            "discount 0% → 15%",
            "Private room none → ×2",
        ]);
        TariffApprovals.Changes(InUse, InUse with { IsActive = false }, beds).Single().Value
            .ShouldBe("Taken out of use: its patients are charged by their type's default plan");
        TariffApprovals.Changes(InUse, TariffPlanTerms.Create(TariffType.Corporate, null, 10m, false, true, [new(PrivateRoom, 1.5m)]), beds)
            .Single().Words.ShouldBe("ICU bed ×2.5 → none", "a multiplier taken away is a change too");
        TariffApprovals.Changes(InUse, InUse, beds).ShouldBeEmpty();
    }

    [Fact]
    public void An_approval_is_made_only_over_the_very_rows_it_replaces()
    {
        var from = new DateOnly(2031, 1, 1);
        TariffPriceRow Row(int n, decimal price, uint version = 1) => new(Guid.Parse($"0c000000-0000-0000-0000-00000000000{n}"), PrivateRoom, price, from, null, version);
        IReadOnlyList<TariffPriceRow> asked = [Row(1, 300m), Row(2, 500m)];

        TariffApprovals.Same([Row(2, 500m), Row(1, 300m)], asked).ShouldBeTrue("in any order");
        TariffApprovals.Same([Row(1, 300m), Row(2, 550m)], asked).ShouldBeFalse("a price changed");
        TariffApprovals.Same([Row(1, 300m), Row(2, 500m, version: 2)], asked).ShouldBeFalse("saved again, even at the same price");
        TariffApprovals.Same([Row(1, 300m)], asked).ShouldBeFalse("one removed");
        TariffApprovals.Same([Row(1, 300m), Row(2, 500m), Row(3, 700m)], asked).ShouldBeFalse("one added");
        TariffApprovals.Same([], []).ShouldBeTrue("a new price for a service with none");
    }
}
