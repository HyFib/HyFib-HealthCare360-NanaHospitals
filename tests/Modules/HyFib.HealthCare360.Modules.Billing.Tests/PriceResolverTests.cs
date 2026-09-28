using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class PriceResolverTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly PricingItem Procedure = new(Guid.CreateVersion7(), ServiceCategory.Procedure, 1000m);

    [Fact]
    public void Plan_price_effective_on_the_service_date_beats_the_base_price()
    {
        var quote = PriceResolver.Resolve(Procedure, [new PlanPrice(Guid.CreateVersion7(), 800m, Today.AddDays(-10), null)], Today);

        quote.UnitPrice.ShouldBe(800m);
        quote.Source.ShouldBe(PriceSource.TariffPlan);
    }

    [Fact]
    public void Base_price_applies_when_the_plan_does_not_list_the_service()
    {
        var quote = PriceResolver.Resolve(Procedure, [], Today);

        quote.UnitPrice.ShouldBe(1000m);
        quote.Source.ShouldBe(PriceSource.BasePrice);
        quote.TariffPriceId.ShouldBeNull();
    }

    [Fact]
    public void The_latest_revision_in_force_wins()
    {
        var revised = Guid.CreateVersion7();
        var quote = PriceResolver.Resolve(Procedure,
        [
            new PlanPrice(Guid.CreateVersion7(), 700m, Today.AddYears(-1), null),
            new PlanPrice(revised, 750m, Today.AddDays(-1), null),
            new PlanPrice(Guid.CreateVersion7(), 900m, Today.AddDays(5), null),
        ], Today);

        quote.UnitPrice.ShouldBe(750m);
        quote.TariffPriceId.ShouldBe(revised);
    }

    [Fact]
    public void Effective_to_is_inclusive_and_the_next_day_falls_back_to_base_price()
    {
        PlanPrice[] prices = [new PlanPrice(Guid.CreateVersion7(), 600m, Today.AddDays(-30), Today)];

        PriceResolver.Resolve(Procedure, prices, Today).UnitPrice.ShouldBe(600m);
        PriceResolver.Resolve(Procedure, prices, Today.AddDays(1)).UnitPrice.ShouldBe(1000m);
    }

    [Theory]
    [InlineData(ServiceCategory.Procedure, 1500)]
    [InlineData(ServiceCategory.Surgery, 1500)]
    [InlineData(ServiceCategory.Nursing, 1500)]
    [InlineData(ServiceCategory.DoctorVisit, 1500)]
    [InlineData(ServiceCategory.Laboratory, 1000)]
    [InlineData(ServiceCategory.BedCharge, 1000)]
    [InlineData(ServiceCategory.Consultation, 1000)]
    public void Bed_class_multiplier_applies_only_to_room_category_priced_services(ServiceCategory category, decimal expected)
    {
        var quote = PriceResolver.Resolve(new PricingItem(Guid.CreateVersion7(), category, 1000m), [], Today, bedMultiplier: 1.5m);

        quote.UnitPrice.ShouldBe(expected);
    }

    [Fact]
    public void Multiplied_prices_round_half_away_from_zero_to_paise()
    {
        var item = new PricingItem(Guid.CreateVersion7(), ServiceCategory.Nursing, 333.33m);

        PriceResolver.Resolve(item, [], Today, bedMultiplier: 1.5m).UnitPrice.ShouldBe(500.00m);
    }

    [Fact]
    public void Free_follow_up_is_priced_at_zero()
    {
        var quote = PriceResolver.Resolve(Procedure, [new PlanPrice(Guid.CreateVersion7(), 800m, Today, null)], Today, isFreeFollowUp: true);

        quote.UnitPrice.ShouldBe(0m);
        quote.Source.ShouldBe(PriceSource.FreeFollowUp);
    }

    [Fact]
    public void Explicit_override_is_honoured_and_never_multiplied()
    {
        var quote = PriceResolver.Resolve(Procedure, [new PlanPrice(Guid.CreateVersion7(), 800m, Today, null)], Today, bedMultiplier: 2m, unitPriceOverride: 123.456m);

        quote.UnitPrice.ShouldBe(123.46m);
        quote.Source.ShouldBe(PriceSource.Override);
        quote.Multiplier.ShouldBe(1m);
    }

    [Fact]
    public void Negative_override_is_clamped_to_zero()
    {
        PriceResolver.Resolve(Procedure, [], Today, unitPriceOverride: -5m).UnitPrice.ShouldBe(0m);
    }

    [Theory]
    [InlineData(true, 700, null)]
    [InlineData(false, 700, 700)]
    [InlineData(false, 0, null)]
    public void Consultation_uses_payer_tariff_then_doctor_fee_then_catalogue(bool payerPlanHasPrice, decimal fee, int? expected)
    {
        PriceResolver.ConsultationOverride(payerPlanHasPrice, fee).ShouldBe(expected is null ? null : (decimal?)expected);
    }
}
