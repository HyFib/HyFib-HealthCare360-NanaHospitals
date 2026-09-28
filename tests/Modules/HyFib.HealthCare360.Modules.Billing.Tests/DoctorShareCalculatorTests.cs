using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class DoctorShareCalculatorTests
{
    private static readonly Guid Doctor = Guid.CreateVersion7();
    private static readonly Guid OtherDoctor = Guid.CreateVersion7();
    private static readonly Guid Consultation = Guid.CreateVersion7();
    private static readonly DateOnly YearStart = new(2026, 1, 1);
    private static readonly DateOnly ServiceDay = new(2026, 9, 25);

    private static ShareRuleView Rule(
        decimal value,
        ShareMethod method = ShareMethod.Percentage,
        Guid? practitionerId = null,
        Guid? serviceItemId = null,
        ServiceCategory? category = ServiceCategory.Consultation,
        PayerType? payerType = null,
        DateOnly? from = null,
        DateOnly? to = null) =>
        new(Guid.CreateVersion7(), practitionerId, serviceItemId, category, payerType, method, value, from ?? YearStart, to);

    private static ShareLine Line(decimal basis, decimal quantity = 1m, PayerType payerType = PayerType.Self) =>
        new(Doctor, Consultation, ServiceCategory.Consultation, payerType, quantity, basis, ServiceDay);

    [Fact]
    public void Percentage_share_is_taken_on_the_billed_value()
    {
        DoctorShareCalculator.Calculate([Rule(80m)], Line(500m)).Amount.ShouldBe(400m);
    }

    [Fact]
    public void Fixed_share_is_per_unit_and_never_exceeds_the_billed_value()
    {
        ShareRuleView[] rules = [Rule(250m, ShareMethod.FixedPerUnit)];

        DoctorShareCalculator.Calculate(rules, Line(1_000m, quantity: 2m)).Amount.ShouldBe(500m);
        DoctorShareCalculator.Calculate(rules, Line(300m, quantity: 2m)).Amount.ShouldBe(300m);
    }

    [Fact]
    public void A_doctor_specific_rule_beats_the_hospital_default()
    {
        var result = DoctorShareCalculator.Calculate([Rule(80m), Rule(60m, practitionerId: Doctor)], Line(1_000m));

        result.Amount.ShouldBe(600m);
        result.Rule!.PractitionerId.ShouldBe(Doctor);
    }

    [Fact]
    public void A_service_rule_beats_a_category_rule()
    {
        DoctorShareCalculator.Calculate([Rule(50m), Rule(70m, serviceItemId: Consultation, category: null)], Line(1_000m)).Amount.ShouldBe(700m);
    }

    [Fact]
    public void A_payer_type_rule_applies_only_to_that_payer()
    {
        ShareRuleView[] rules = [Rule(80m), Rule(40m, payerType: PayerType.GovernmentScheme)];

        DoctorShareCalculator.Calculate(rules, Line(1_000m, payerType: PayerType.GovernmentScheme)).Amount.ShouldBe(400m);
        DoctorShareCalculator.Calculate(rules, Line(1_000m, payerType: PayerType.Self)).Amount.ShouldBe(800m);
    }

    [Fact]
    public void Another_doctors_rule_never_applies()
    {
        var result = DoctorShareCalculator.Calculate([Rule(90m, practitionerId: OtherDoctor)], Line(1_000m));

        result.Amount.ShouldBe(0m);
        result.Rule.ShouldBeNull();
    }

    [Fact]
    public void Rules_outside_their_dates_are_ignored_and_the_latest_rule_wins_a_tie()
    {
        ShareRuleView[] rules =
        [
            Rule(90m, to: ServiceDay.AddDays(-1)),
            Rule(60m, from: YearStart),
            Rule(65m, from: ServiceDay.AddDays(-10)),
            Rule(99m, from: ServiceDay.AddDays(1)),
        ];

        DoctorShareCalculator.Calculate(rules, Line(1_000m)).Amount.ShouldBe(650m);
    }

    [Fact]
    public void A_free_follow_up_earns_no_share()
    {
        DoctorShareCalculator.Calculate([Rule(250m, ShareMethod.FixedPerUnit)], Line(0m)).Amount.ShouldBe(0m);
    }

    [Fact]
    public void Shares_round_half_away_from_zero()
    {
        DoctorShareCalculator.Calculate([Rule(33.33m)], Line(100.05m)).Amount.ShouldBe(33.35m);
    }
}
