using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A rule of the non-payable list (IRDAI non-payables), as a request to change the list carries it (maker-checker): which
/// item of the list it is, so the same item waits for approval once, and which charges it reaches, as bills decide it.
/// </summary>
public sealed class NonPayableRuleTests
{
    private static readonly Guid Payer = Guid.NewGuid();
    private static readonly Guid Kit = Guid.NewGuid();

    [Fact]
    public void The_same_rule_is_the_same_item_whatever_its_notes()
    {
        var rule = new NonPayableRule(Payer, Kit, null, "Kits are the patient's");
        var again = rule with { Notes = "IRDAI list" };

        rule.SameRule(again).ShouldBeTrue();
        again.ItemId().ShouldBe(rule.ItemId());
        new NonPayableRule(null, null, ServiceCategory.Consumable, null).ItemId()
            .ShouldBe(new NonPayableRule(null, null, ServiceCategory.Consumable, "Every payer").ItemId());
    }

    [Fact]
    public void Another_payer_service_or_category_is_another_item()
    {
        var rule = new NonPayableRule(Payer, Kit, null, null);
        NonPayableRule[] others =
        [
            rule with { PayerId = null },
            rule with { PayerId = Guid.NewGuid() },
            rule with { ServiceItemId = Guid.NewGuid() },
            new(Payer, null, ServiceCategory.Consumable, null),
            new(Payer, null, ServiceCategory.Miscellaneous, null),
            new(null, null, ServiceCategory.Consumable, null),
        ];

        others.ShouldAllBe(o => !o.SameRule(rule));
        others.Select(o => o.ItemId()).Append(rule.ItemId()).Distinct().Count().ShouldBe(others.Length + 1);
    }

    [Fact]
    public void A_rule_reaches_its_service_or_category_for_its_payer_or_for_every_payer()
    {
        var kitForPayer = new NonPayableRule(Payer, Kit, null, null);
        kitForPayer.Reaches(Payer, Kit, ServiceCategory.Consumable).ShouldBeTrue();
        kitForPayer.Reaches(Guid.NewGuid(), Kit, ServiceCategory.Consumable).ShouldBeFalse("another payer");
        kitForPayer.Reaches(Payer, Guid.NewGuid(), ServiceCategory.Consumable).ShouldBeFalse("another service of the same category");

        var consumablesForEveryPayer = new NonPayableRule(null, null, ServiceCategory.Consumable, null);
        consumablesForEveryPayer.Reaches(Guid.NewGuid(), Guid.NewGuid(), ServiceCategory.Consumable).ShouldBeTrue();
        consumablesForEveryPayer.Reaches(Payer, Kit, ServiceCategory.Miscellaneous).ShouldBeFalse("another category");
    }
}
