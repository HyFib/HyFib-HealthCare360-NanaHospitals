using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

public sealed class ReferenceRangeSelectorTests
{
    [Fact]
    public void Sex_specific_interval_beats_the_sex_neutral_one()
    {
        var neutral = Range(10, 20);
        var female = Range(12, 15, gender: Gender.Female);

        ReferenceRangeSelector.Select([neutral, female], Gender.Female, 10_000).ShouldBe(female);
        ReferenceRangeSelector.Select([neutral, female], Gender.Male, 10_000).ShouldBe(neutral);
    }

    [Fact]
    public void Narrowest_age_band_wins()
    {
        var allAges = Range(1, 2);
        var child = Range(3, 4, from: Years(6), to: Years(12));

        ReferenceRangeSelector.Select([allAges, child], Gender.Male, Years(8)).ShouldBe(child);
        ReferenceRangeSelector.Select([allAges, child], Gender.Male, Years(30)).ShouldBe(allAges);
    }

    [Fact]
    public void Age_band_includes_its_start_and_excludes_its_end()
    {
        var neonate = Range(14, 22, from: 0, to: 30);
        var infant = Range(9.5m, 14, from: 30, to: 180);

        ReferenceRangeSelector.Select([neonate, infant], Gender.Female, 29).ShouldBe(neonate);
        ReferenceRangeSelector.Select([neonate, infant], Gender.Female, 30).ShouldBe(infant);
    }

    [Fact]
    public void Unknown_age_only_uses_all_age_intervals()
    {
        var adult = Range(13, 17, gender: Gender.Male, from: Years(18));
        var allAges = Range(12, 18);

        ReferenceRangeSelector.Select([adult, allAges], Gender.Male, null).ShouldBe(allAges);
        ReferenceRangeSelector.Select([adult], Gender.Male, null).ShouldBeNull();
    }

    [Fact]
    public void Unknown_sex_only_uses_sex_neutral_intervals()
    {
        var male = Range(13, 17, gender: Gender.Male);
        var female = Range(12, 15, gender: Gender.Female);

        ReferenceRangeSelector.Select([male, female], Gender.Unknown, 20_000).ShouldBeNull();
        ReferenceRangeSelector.Select([male, female, Range(12, 17)], Gender.Unknown, 20_000)!.Gender.ShouldBeNull();
    }

    [Fact]
    public void Age_in_days_is_counted_to_the_collection_date()
    {
        ReferenceRangeSelector.AgeInDays(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 25)).ShouldBe(24);
        ReferenceRangeSelector.AgeInDays(null, new DateOnly(2026, 9, 25)).ShouldBeNull();
    }

    [Theory]
    [InlineData(Gender.Male, 25, 13.0, 17.0)]
    [InlineData(Gender.Female, 25, 12.0, 15.0)]
    [InlineData(Gender.Female, 10, 11.5, 15.5)]
    [InlineData(Gender.Male, 3, 11.0, 14.0)]
    public void Seeded_haemoglobin_intervals_follow_age_and_sex(Gender gender, int ageYears, double low, double high)
    {
        var range = ReferenceRangeSelector.Select(SeededRanges("HB"), gender, Years(ageYears))!;

        range.Low.ShouldBe((decimal)low);
        range.High.ShouldBe((decimal)high);
        range.CriticalLow.ShouldBe(7.0m);
    }

    [Fact]
    public void Seeded_neonatal_potassium_has_its_own_critical_limits()
    {
        var neonate = ReferenceRangeSelector.Select(SeededRanges("K"), Gender.Male, 5)!;
        var adult = ReferenceRangeSelector.Select(SeededRanges("K"), Gender.Male, Years(40))!;

        neonate.CriticalHigh.ShouldBe(7.0m);
        adult.CriticalHigh.ShouldBe(6.5m);
        adult.CriticalLow.ShouldBe(2.5m);
    }

    [Fact]
    public void Validation_rejects_inconsistent_intervals()
    {
        Range(5, 3).Validate().Error!.Code.ShouldBe("range.limits");
        Range(3, 5, from: 100, to: 50).Validate().Error!.Code.ShouldBe("range.age_band");
        Range(3.5m, 5.1m, criticalLow: 4.0m).Validate().Error!.Code.ShouldBe("range.critical_low");
        Range(3.5m, 5.1m, criticalHigh: 5.0m).Validate().Error!.Code.ShouldBe("range.critical_high");
        Answers(["Negative"], ["negative"]).Validate().Error!.Code.ShouldBe("range.values");
        Range(3.5m, 5.1m, 2.5m, 6.5m).Validate().IsSuccess.ShouldBeTrue();
    }
}
