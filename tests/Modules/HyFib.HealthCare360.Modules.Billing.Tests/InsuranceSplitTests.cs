using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class InsuranceSplitTests
{
    [Fact]
    public void Co_pay_is_the_patient_share_of_payable_services()
    {
        var totals = BillTotalsCalculator.Calculate([Line(10_000m)], BillDiscountInput.None, new SplitTerms(true, 10m, null));

        totals.CoPay.ShouldBe(1_000m);
        totals.PayerAmount.ShouldBe(9_000m);
        totals.PatientAmount.ShouldBe(1_000m);
        totals.Net.ShouldBe(10_000m);
    }

    [Fact]
    public void Approved_amount_caps_the_payer_share()
    {
        var totals = BillTotalsCalculator.Calculate([Line(50_000m)], BillDiscountInput.None, new SplitTerms(true, 0m, 40_000m));

        totals.PayerAmount.ShouldBe(40_000m);
        totals.PatientAmount.ShouldBe(10_000m);
    }

    [Fact]
    public void Non_payable_items_are_paid_by_the_patient()
    {
        var totals = BillTotalsCalculator.Calculate([Line(10_000m), Line(500m, payable: false)], BillDiscountInput.None, new SplitTerms(true, 0m, null));

        totals.PayerAmount.ShouldBe(10_000m);
        totals.NonPayable.ShouldBe(500m);
        totals.PatientAmount.ShouldBe(500m);
    }

    [Fact]
    public void Round_off_is_applied_only_to_the_patient_share()
    {
        var totals = BillTotalsCalculator.Calculate([Line(10_000.40m)], BillDiscountInput.None, new SplitTerms(true, 5m, null));

        totals.CoPay.ShouldBe(500.02m);
        totals.PayerAmount.ShouldBe(9_500.38m);
        totals.PatientAmount.ShouldBe(500m);
        totals.RoundOff.ShouldBe(-0.02m);
        totals.Net.ShouldBe(10_000.38m);
    }

    [Fact]
    public void A_fully_covered_bill_leaves_nothing_for_the_patient_even_with_paise()
    {
        var totals = BillTotalsCalculator.Calculate([Line(10_000.40m)], BillDiscountInput.None, new SplitTerms(true, 0m, 20_000m));

        totals.PayerAmount.ShouldBe(10_000.40m);
        totals.PatientAmount.ShouldBe(0m);
        totals.RoundOff.ShouldBe(0m);
    }

    [Fact]
    public void Self_pay_bills_have_no_payer_share()
    {
        var totals = BillTotalsCalculator.Calculate([Line(1_000m)], BillDiscountInput.None, SplitTerms.SelfPay);

        totals.PayerAmount.ShouldBe(0m);
        totals.NonPayable.ShouldBe(0m);
        totals.PatientAmount.ShouldBe(1_000m);
    }

    [Theory]
    [InlineData(PayerType.Insurance, null, 0)]
    [InlineData(PayerType.Insurance, 25000, 25000)]
    [InlineData(PayerType.Corporate, null, null)]
    [InlineData(PayerType.Corporate, 30000, 30000)]
    [InlineData(PayerType.GovernmentScheme, null, null)]
    [InlineData(PayerType.Self, 5000, 0)]
    public void Payer_coverage_limit_depends_on_the_payer_type(PayerType payerType, int? approved, int? expected)
    {
        PayerCoverage.CoverageLimit(payerType, approved).ShouldBe(expected is null ? null : (decimal?)expected);
    }

    [Fact]
    public void A_bill_splits_only_when_a_third_party_payer_is_named()
    {
        var withPayer = DraftBill(new BillPayerTerms(PayerType.Insurance, Guid.CreateVersion7(), null, null, null, 10m, 5_000m), Charge(8_000m));
        var withoutPayer = DraftBill(new BillPayerTerms(PayerType.Insurance, null, null, null, null, 10m, 5_000m), Charge(8_000m));

        withPayer.PayerAmount.ShouldBe(5_000m);
        withPayer.PatientAmount.ShouldBe(3_000m);
        withoutPayer.PayerAmount.ShouldBe(0m);
        withoutPayer.PatientAmount.ShouldBe(8_000m);
    }
}
