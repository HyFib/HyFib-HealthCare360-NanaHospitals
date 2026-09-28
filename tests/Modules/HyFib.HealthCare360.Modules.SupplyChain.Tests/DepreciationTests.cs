using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

public sealed class DepreciationTests
{
    private static readonly DateOnly Start = new(2024, 4, 1);

    [Fact]
    public void Straight_line_charges_an_equal_amount_down_to_salvage()
    {
        var schedule = DepreciationCalculator.StraightLine(100_000m, 10_000m, 5, Start);

        schedule.Select(y => y.Depreciation).ShouldAllBe(d => d == 18_000m);
        schedule.Select(y => y.Closing).ShouldBe([82_000m, 64_000m, 46_000m, 28_000m, 10_000m]);
        schedule[0].From.ShouldBe(Start);
        schedule[0].To.ShouldBe(new DateOnly(2025, 3, 31));
    }

    [Fact]
    public void Straight_line_last_year_absorbs_rounding()
    {
        var schedule = DepreciationCalculator.StraightLine(1_000m, 0m, 3, Start);

        schedule.Select(y => y.Depreciation).ShouldBe([333.33m, 333.33m, 333.34m]);
        schedule[^1].Closing.ShouldBe(0m);
    }

    [Fact]
    public void Book_value_is_pro_rated_inside_the_year()
    {
        var schedule = DepreciationCalculator.StraightLine(100_000m, 10_000m, 5, Start);

        // Year 3 runs 2026-04-01..2027-03-31; on 2026-09-30, 183 of 365 days have elapsed.
        DepreciationCalculator.BookValue(schedule, 100_000m, new DateOnly(2026, 9, 30)).ShouldBe(54_975.34m);
        DepreciationCalculator.BookValue(schedule, 100_000m, new DateOnly(2024, 1, 1)).ShouldBe(100_000m);
        DepreciationCalculator.BookValue(schedule, 100_000m, new DateOnly(2035, 1, 1)).ShouldBe(10_000m);
    }

    [Fact]
    public void Written_down_value_charges_a_percentage_of_the_opening_value()
    {
        var schedule = DepreciationCalculator.WrittenDownValue(100_000m, 5_000m, 15m, 3, Start);

        schedule.Select(y => y.Depreciation).ShouldBe([15_000m, 12_750m, 10_837.50m]);
        schedule[^1].Closing.ShouldBe(61_412.50m);
    }

    [Fact]
    public void Written_down_value_never_goes_below_salvage()
    {
        var schedule = DepreciationCalculator.WrittenDownValue(10_000m, 5_000m, 40m, 10, Start);

        schedule.Count.ShouldBe(2);
        schedule[1].Depreciation.ShouldBe(1_000m);
        schedule[^1].Closing.ShouldBe(5_000m);
    }

    [Fact]
    public void Wdv_rate_reaches_salvage_at_the_end_of_life()
    {
        var rate = DepreciationCalculator.WdvRateFor(100_000m, 10_000m, 5);
        rate.ShouldBe(36.90m);

        var schedule = DepreciationCalculator.WrittenDownValue(100_000m, 10_000m, rate, 5, Start);
        schedule[^1].Closing.ShouldBeInRange(9_990m, 10_010m);
    }

    [Fact]
    public void Invalid_inputs_are_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => DepreciationCalculator.StraightLine(1_000m, 2_000m, 5, Start));
        Should.Throw<ArgumentOutOfRangeException>(() => DepreciationCalculator.StraightLine(1_000m, 0m, 0, Start));
        Should.Throw<ArgumentOutOfRangeException>(() => DepreciationCalculator.WrittenDownValue(1_000m, 0m, 0m, 5, Start));
    }

    [Fact]
    public void Asset_book_value_follows_its_method_and_is_zero_once_disposed()
    {
        var asset = new Asset
        {
            PurchaseCost = 100_000m,
            SalvageValue = 5_000m,
            PurchaseDate = Start,
            UsefulLifeYears = 10,
            DepreciationMethod = DepreciationMethod.WrittenDownValue,
            DepreciationRatePercent = 15m,
        };

        // End of the first year (the as-of day counts as elapsed).
        AssetService.Depreciate(asset, new DateOnly(2025, 3, 31)).BookValue.ShouldBe(85_000m);

        asset.Condemn(new DateOnly(2026, 1, 1)).IsSuccess.ShouldBeTrue();
        asset.Dispose(new DateOnly(2026, 2, 1), 20_000m, "Auction").IsSuccess.ShouldBeTrue();
        AssetService.Depreciate(asset, new DateOnly(2026, 3, 1)).BookValue.ShouldBe(0m);
    }
}
