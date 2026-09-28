using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>Returns posted as negative-quantity credit lines (for example ward stock sent back to the IP pharmacy).</summary>
public sealed class ReturnsAndCreditsTests
{
    private static readonly Guid Drugs = Guid.CreateVersion7();
    private static readonly Guid Implant = Guid.CreateVersion7();

    private static PackageUsage Use(Guid item, decimal quantity, decimal unitPrice, int day, ServiceCategory category = ServiceCategory.Consumable) =>
        new(Guid.CreateVersion7(), item, category, quantity, unitPrice, Money.Round(quantity * unitPrice), Now.AddDays(day));

    [Fact]
    public void A_full_return_mirrors_its_sale_so_the_two_net_to_zero()
    {
        var sale = ChargeAmounts.Calculate(3m, 33.33m, 10m, 12m);
        var credit = ChargeAmounts.Calculate(-3m, 33.33m, 10m, 12m);

        credit.Gross.ShouldBe(-99.99m);
        credit.Discount.ShouldBe(-sale.Discount);
        credit.Cgst.ShouldBe(-sale.Cgst);
        credit.Sgst.ShouldBe(-sale.Sgst);
        (sale.Net + credit.Net).ShouldBe(0m);
    }

    [Fact]
    public void A_negative_quantity_charge_is_a_credit()
    {
        var credit = Charge(50m, quantity: -2m);

        credit.IsCredit.ShouldBeTrue();
        credit.GrossAmount.ShouldBe(-100m);
        credit.NetAmount.ShouldBe(-100m);
    }

    [Fact]
    public void A_return_is_credited_at_the_sales_concession_and_takes_its_share_of_the_bill_discount()
    {
        var totals = BillTotalsCalculator.Calculate(
            [Line(1_000m, tariffPercent: 10m), Line(-200m, tariffPercent: 10m)], new BillDiscountInput(10m, null), SplitTerms.SelfPay);

        totals.Lines[1].TariffDiscount.ShouldBe(-20m);
        totals.Lines[1].BillDiscountShare.ShouldBe(-18m);
        totals.Lines[1].Net.ShouldBe(-162m);
        totals.BillDiscount.ShouldBe(72m);
        totals.Net.ShouldBe(648m);
    }

    [Fact]
    public void Staff_line_discounts_never_apply_to_a_return()
    {
        var totals = BillTotalsCalculator.Calculate([Line(500m), Line(-100m, lineDiscount: 50m)], BillDiscountInput.None, SplitTerms.SelfPay);

        totals.LineDiscount.ShouldBe(0m);
        totals.Net.ShouldBe(400m);
    }

    [Fact]
    public void The_part_of_a_return_the_package_absorbed_goes_back_to_the_package()
    {
        var totals = BillTotalsCalculator.Calculate([Line(-1_500m, covered: -500m)], BillDiscountInput.None, SplitTerms.SelfPay);

        totals.Lines[0].Billable.ShouldBe(-1_000m);
        totals.PackageCovered.ShouldBe(-500m);
        totals.PatientAmount.ShouldBe(-1_000m);
    }

    [Fact]
    public void Returns_alone_earn_no_discount_and_no_negative_co_pay()
    {
        var totals = BillTotalsCalculator.Calculate([Line(-150.40m)], new BillDiscountInput(10m, null), new SplitTerms(true, 10m, null));

        totals.BillDiscount.ShouldBe(0m);
        totals.CoPay.ShouldBe(0m);
        totals.PayerAmount.ShouldBe(0m);
        totals.PatientAmount.ShouldBe(-150m);
        totals.RoundOff.ShouldBe(0.40m);
    }

    [Fact]
    public void A_bill_of_returns_owes_the_patient_a_refund()
    {
        var bill = FinalizedBill(Charge(75.20m, quantity: -2m));

        bill.PatientAmount.ShouldBe(-150m);
        bill.DueAmount.ShouldBe(0m);
        bill.RefundableAmount.ShouldBe(150m);
        bill.Status.ShouldBe(BillStatus.Paid);
    }

    [Fact]
    public void Package_cover_on_a_return_stays_between_its_value_and_zero()
    {
        var bill = DraftBill(null, Charge(100m, quantity: -3m));
        var line = bill.Lines.Single();

        bill.ApplyPackageCoverage(new Dictionary<Guid, decimal> { [line.Id] = -500m });
        line.PackageCoveredAmount.ShouldBe(-300m);

        bill.ApplyPackageCoverage(new Dictionary<Guid, decimal> { [line.Id] = 50m });
        line.PackageCoveredAmount.ShouldBe(0m);
    }

    [Fact]
    public void A_return_first_refunds_what_the_patient_paid_beyond_the_package_then_restores_its_allowance()
    {
        var first = Use(Drugs, 1m, 3_000m, day: 0);
        var second = Use(Drugs, 1m, 3_000m, day: 1);
        var returned = Use(Drugs, -1m, 1_500m, day: 2);
        var later = Use(Drugs, 1m, 800m, day: 3);
        PackageRule[] rules = [new PackageRule(null, ServiceCategory.Consumable, null, 5_000m)];

        var outcome = PackageCalculator.Allocate(40_000m, rules, [first, second, returned, later]);

        var credit = outcome.Lines.Single(l => l.Key == returned.Key);
        credit.Covered.ShouldBe(-500m);
        credit.Excess.ShouldBe(-1_000m);
        outcome.Lines.Single(l => l.Key == later.Key).Covered.ShouldBe(500m);
        outcome.Covered.ShouldBe(5_000m);
        outcome.Excess.ShouldBe(300m);
        outcome.Actuals.ShouldBe(5_300m);
    }

    [Fact]
    public void A_return_within_the_excess_is_credited_to_the_patient_in_full()
    {
        PackageRule[] rules = [new PackageRule(null, ServiceCategory.Consumable, null, 5_000m)];
        var returned = Use(Drugs, -1m, 500m, day: 2);

        var outcome = PackageCalculator.Allocate(40_000m, rules, [Use(Drugs, 1m, 3_000m, day: 0), Use(Drugs, 1m, 3_000m, day: 1), returned]);

        var credit = outcome.Lines.Single(l => l.Key == returned.Key);
        credit.Covered.ShouldBe(0m);
        credit.Excess.ShouldBe(-500m);
    }

    [Fact]
    public void A_returned_unit_gives_a_quantity_cap_back()
    {
        PackageRule[] rules = [new PackageRule(Implant, null, 1m, null)];
        var returned = Use(Implant, -1m, 50_000m, day: 1);
        var replacement = Use(Implant, 1m, 48_000m, day: 2);

        var outcome = PackageCalculator.Allocate(90_000m, rules, [Use(Implant, 1m, 50_000m, day: 0), returned, replacement]);

        outcome.Lines.Single(l => l.Key == returned.Key).Covered.ShouldBe(-50_000m);
        outcome.Lines.Single(l => l.Key == replacement.Key).Covered.ShouldBe(48_000m);
        outcome.Excess.ShouldBe(0m);
    }

    [Fact]
    public void Returns_of_services_outside_the_package_are_credited_in_full()
    {
        var returned = Use(Drugs, -2m, 120m, day: 1, ServiceCategory.Laboratory);

        var outcome = PackageCalculator.Allocate(40_000m, [new PackageRule(null, ServiceCategory.Consumable, null, 5_000m)], [returned]);

        outcome.Lines[0].Covered.ShouldBe(0m);
        outcome.Lines[0].Excess.ShouldBe(-240m);
        outcome.Lines[0].RuleIndex.ShouldBeNull();
    }
}
