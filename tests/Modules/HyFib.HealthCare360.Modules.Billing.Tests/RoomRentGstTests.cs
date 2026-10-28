using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// GST on hospital room rent: health care is exempt, but a room other than an intensive care bed, charged above ₹5,000 a
/// day, is taxed at 5% on the whole charge (notification 12/2017-CT(R) entry 74 and 11/2017-CT(R) entry 31A, as amended
/// from 18 July 2022).
/// </summary>
public sealed class RoomRentGstTests
{
    [Fact]
    public void A_room_charged_above_five_thousand_a_day_is_taxed_at_five_percent_on_the_whole_charge()
    {
        var deluxe = TestData.Charge(6_500m, category: ServiceCategory.BedCharge, room: RoomCharge.Room);

        deluxe.TaxRate.ShouldBe(5m);
        (deluxe.CgstAmount, deluxe.SgstAmount).ShouldBe((162.50m, 162.50m));
        deluxe.NetAmount.ShouldBe(6_825m);
    }

    [Theory]
    [InlineData(5_000.00, 0)]
    [InlineData(5_000.01, 5)]
    [InlineData(4_000.00, 0)]
    public void Only_a_day_charged_above_five_thousand_is_taxed(decimal dayCharge, decimal rate) =>
        TestData.Charge(dayCharge, category: ServiceCategory.BedCharge, room: RoomCharge.Room).TaxRate.ShouldBe(rate);

    [Fact]
    public void An_intensive_care_bed_is_exempt_whatever_it_costs() =>
        TestData.Charge(8_000m, category: ServiceCategory.BedCharge, room: RoomCharge.IntensiveCare).TaxAmount.ShouldBe(0m);

    [Fact]
    public void The_plans_concession_decides_whether_a_day_is_over_the_line_and_the_tax_is_on_what_is_charged()
    {
        // ₹5,500 less the plan's 10% is ₹4,950 a day: under the line, exempt.
        TestData.Charge(5_500m, category: ServiceCategory.BedCharge, tariffDiscountPercent: 10m, room: RoomCharge.Room).TaxAmount.ShouldBe(0m);

        // ₹6,000 less 10% is ₹5,400: taxed, on ₹5,400.
        var taxed = TestData.Charge(6_000m, category: ServiceCategory.BedCharge, tariffDiscountPercent: 10m, room: RoomCharge.Room);
        taxed.TaxRate.ShouldBe(5m);
        taxed.TaxAmount.ShouldBe(270m);
    }

    [Fact]
    public void A_rooms_own_rate_in_the_service_master_gives_way_to_the_rule()
    {
        TestData.Charge(4_000m, category: ServiceCategory.BedCharge, taxRate: 18m, room: RoomCharge.Room).TaxRate.ShouldBe(0m);
        TestData.Charge(8_000m, category: ServiceCategory.BedCharge, taxRate: 18m, room: RoomCharge.IntensiveCare).TaxRate.ShouldBe(0m);

        // A charge that is not a day of a room keeps the service's rate.
        TestData.Charge(8_000m, taxRate: 18m).TaxRate.ShouldBe(18m);
    }

    [Fact]
    public void A_room_priced_again_on_another_plan_is_taxed_by_its_new_price()
    {
        var deluxe = TestData.Charge(6_500m, category: ServiceCategory.BedCharge, room: RoomCharge.Room);

        deluxe.ApplyPrice(new PriceQuote(4_800m, PriceSource.TariffPlan, 1m, null), 0m, 0m, null);

        (deluxe.TaxRate, deluxe.TaxAmount, deluxe.NetAmount).ShouldBe((0m, 0m, 4_800m));
    }
}
