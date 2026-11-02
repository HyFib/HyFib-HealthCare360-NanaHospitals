using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// The plan a patient is priced on changes only when two people agree: the rule's parts, row by row. A payer's terms wait,
/// except taking it out of use; a new payer waits out of use with nothing that prices anyone; the approver reads every
/// change in words. A stay is billed on its payer's plan, and any other plan waits, read against the payer's plan. The
/// integration tests drive them over the API; the rows they leave out are here.
/// </summary>
public sealed class PayerTermsTests
{
    private static readonly Guid Star = Guid.Parse("0d000000-0000-0000-0000-000000000001");
    private static readonly Guid Cghs = Guid.Parse("0d000000-0000-0000-0000-000000000002");
    private static readonly Guid General = Guid.Parse("0d000000-0000-0000-0000-000000000003");
    private static readonly Guid Staff = Guid.Parse("0d000000-0000-0000-0000-000000000004");
    private static readonly Guid Insurer = Guid.Parse("0e000000-0000-0000-0000-000000000001");
    private static readonly Guid Policy = Guid.Parse("0e000000-0000-0000-0000-000000000002");

    private static readonly PayerTerms InUse = new(PayerCategory.Insurer, Star, 10m, IsActive: true);

    [Fact]
    public void A_new_payer_waits_out_of_use_with_no_plan_or_co_pay()
    {
        var asked = new PayerTerms(PayerCategory.Corporate, Star, 10m, IsActive: true);
        var now = asked.OutOfUse();

        (now.TariffPlanId, now.DefaultCoPayPercent, now.IsActive).ShouldBe(((Guid?)null, 0m, false));
        now.Category.ShouldBe(PayerCategory.Corporate, "it prices nobody while it is out of use");
        now.ShouldNotBe(asked);
        new PayerTerms(PayerCategory.Corporate, null, 0m, false).OutOfUse()
            .ShouldBe(new PayerTerms(PayerCategory.Corporate, null, 0m, false), "nothing to ask for");
    }

    [Fact]
    public void Only_taking_a_payer_out_of_use_is_made_at_once()
    {
        InUse.Narrowed(InUse with { IsActive = false }).ShouldBe(InUse with { IsActive = false });
        InUse.Narrowed(InUse with { TariffPlanId = Cghs }).ShouldBe(InUse, "a plan waits");
        InUse.Narrowed(InUse with { TariffPlanId = null }).ShouldBe(InUse, "no plan of its own waits too: the default's prices may differ");
        InUse.Narrowed(InUse with { DefaultCoPayPercent = 0m }).ShouldBe(InUse, "a co-pay waits");
        InUse.Narrowed(InUse with { Category = PayerCategory.Tpa }).ShouldBe(InUse, "a category waits");
        (InUse with { IsActive = false }).Narrowed(InUse).IsActive.ShouldBeFalse("putting it in use waits");
    }

    [Fact]
    public void The_approver_reads_every_change_to_a_payer_in_words()
    {
        var plans = new Dictionary<Guid, PayerApprovals.PlanName> { [Star] = new("Star Health TPA", true), [Cghs] = new("CGHS 2026", false) };
        var changes = PayerApprovals.Changes(new PayerTerms(PayerCategory.Insurer, null, 0m, false), new PayerTerms(PayerCategory.Tpa, Cghs, 10m, true), plans)
            .ToList();

        changes.Select(c => c.Words).ShouldBe(["put in use", "category Insurer → TPA", "plan none → CGHS 2026", "co-pay 0% → 10%"]);
        changes.Single(c => c.Label == "Tariff plan").Value
            .ShouldBe("none of its own: its type's default plan → CGHS 2026 (out of use: its type's default plan prices its patients until it is put in use)");
        PayerApprovals.Changes(InUse, InUse with { IsActive = false }, plans).Single().Value.ShouldBe("Taken out of use: no new visit or stay is billed to it");
        PayerApprovals.Changes(InUse, InUse with { TariffPlanId = null }, plans).Single().Words.ShouldBe("plan Star Health TPA → none");
        PayerApprovals.Changes(InUse, InUse, plans).ShouldBeEmpty();
    }

    [Fact]
    public void A_stay_is_billed_on_its_payers_plan_and_another_plan_waits()
    {
        var selfPay = new StayPayerTerms(PayerType.Self, null, null, General);
        StayPlanApprovals.AtOnce(selfPay, selfPay, General).ShouldBeTrue("nothing changes");
        StayPlanApprovals.AtOnce(selfPay, selfPay with { TariffPlanId = Staff }, General).ShouldBeFalse("a concession waits");

        var cashless = new StayPayerTerms(PayerType.Insurance, Insurer, Policy, Star);
        StayPlanApprovals.AtOnce(selfPay, cashless, Star).ShouldBeTrue("cash converted to cashless, on the insurer's plan");
        StayPlanApprovals.AtOnce(selfPay, cashless with { TariffPlanId = General }, Star).ShouldBeFalse("a new payer keeping the old plan waits");

        var approved = selfPay with { TariffPlanId = Staff };
        StayPlanApprovals.AtOnce(approved, approved, General).ShouldBeTrue("a plan approved is kept while the payer stays");
        StayPlanApprovals.AtOnce(approved, approved with { TariffPlanId = General }, General).ShouldBeTrue("back to the payer's plan");
        StayPlanApprovals.AtOnce(cashless, cashless with { PolicyId = null }, Star).ShouldBeTrue("a policy unlinked keeps the payer and plan");
        StayPlanApprovals.AtOnce(cashless with { TariffPlanId = Cghs }, cashless with { TariffPlanId = Cghs, PayerId = Guid.Empty }, Star)
            .ShouldBeFalse("another payer keeping a plan that is not its own waits");
    }

    [Fact]
    public void A_plan_asked_is_read_against_the_payers_plan()
    {
        StayPlanApprovals.Difference(30_000m, 40_000m).ShouldBe("−₹10,000.00 (−25%)");
        StayPlanApprovals.Difference(50_000m, 40_000m).ShouldBe("+₹10,000.00 (+25%)");
        StayPlanApprovals.Difference(1_23_456.5m, 1_00_000m).ShouldBe("+₹23,456.50 (+23.5%)");
        StayPlanApprovals.Difference(100m, 100m).ShouldBe("₹0.00 (0%)");
        StayPlanApprovals.Difference(100m, 0m).ShouldBe("+₹100.00", "nothing to compare with on the payer's plan");
    }
}
