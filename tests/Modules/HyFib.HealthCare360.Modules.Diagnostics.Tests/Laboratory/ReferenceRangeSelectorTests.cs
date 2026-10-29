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

    [Theory]
    [InlineData(Gender.Other)]
    [InlineData(Gender.Unknown)]
    public void A_patient_neither_male_nor_female_gets_the_sex_neutral_interval_when_there_is_one(Gender gender)
    {
        var male = Range(13, 17, gender: Gender.Male);
        var female = Range(12, 15, gender: Gender.Female);
        var neutral = Range(12, 17);

        ReferenceRangeSelector.Select([male, female, neutral], gender, 20_000).ShouldBe(neutral);
        ReferenceRangeSelector.ReadsBothSexes([male, female, neutral], gender, 20_000).ShouldBeFalse();
    }

    [Theory]
    [InlineData(Gender.Other)]
    [InlineData(Gender.Unknown)]
    public void A_patient_neither_male_nor_female_is_read_against_both_intervals_when_only_those_apply(Gender gender)
    {
        // Adult haemoglobin has only a male and a female interval. A transgender patient, or one whose sex is not known
        // (an unidentified casualty), got none: below 7 g/dL there was no critical alert.
        var both = ReferenceRangeSelector.Select(SeededRanges("HB"), gender, Years(30), decimals: 1)!;

        both.Low.ShouldBe(12.0m, "low only when below both intervals");
        both.High.ShouldBe(17.0m, "high only when above both intervals");
        both.CriticalLow.ShouldBe(7.0m);
        both.CriticalHigh.ShouldBe(20.0m);
        both.Text.ShouldBe("Male 13.0 - 17.0; Female 12.0 - 15.0");
        ReferenceRangeSelector.ReadsBothSexes(SeededRanges("HB"), gender, Years(30)).ShouldBeTrue();
    }

    [Fact]
    public void Read_against_both_intervals_the_stricter_critical_limit_holds()
    {
        var male = Range(13, 17, criticalLow: 7, criticalHigh: 20, gender: Gender.Male);
        var female = Range(12, 15, criticalLow: 8, criticalHigh: 19, gender: Gender.Female);

        var both = ReferenceRangeSelector.Select([male, female], Gender.Other, 20_000)!;

        both.CriticalLow.ShouldBe(8m);
        both.CriticalHigh.ShouldBe(19m);
    }

    /// <summary>Only an interval with something to print is printed, and the result is still read against both.</summary>
    [Fact]
    public void Read_against_both_intervals_only_what_can_be_printed_is_printed()
    {
        var male = Range(13, 17, criticalLow: 7, gender: Gender.Male);
        var female = Range(null, null, criticalLow: 6.5m, gender: Gender.Female);

        var both = ReferenceRangeSelector.Select([male, female], Gender.Other, 20_000, decimals: 1)!;

        both.Text.ShouldBe("Male 13.0 - 17.0");
        (both.Low, both.High, both.CriticalLow).ShouldBe((null, null, 7m));
        ReferenceRangeSelector.ReadsBothSexes([male, female], Gender.Other, 20_000).ShouldBeTrue();

        var criticalOnly = Range(null, null, criticalLow: 7, gender: Gender.Male);
        var nothingToPrint = ReferenceRangeSelector.Select([criticalOnly, female], Gender.Unknown, 20_000)!;
        nothingToPrint.Text.ShouldBeNull();
        nothingToPrint.CriticalLow.ShouldBe(7m);
    }

    /// <summary>
    /// A result holds 500 characters of reference text. A long text for one sex is shortened, rather than the other
    /// interval being cut off: the person verifying the result must see both.
    /// </summary>
    [Theory]
    [InlineData(490, 0)]
    [InlineData(0, 490)]
    [InlineData(400, 400)]
    public void Read_against_both_intervals_a_long_text_does_not_crowd_out_the_other_interval(int maleLength, int femaleLength)
    {
        var male = Range(13, 17, gender: Gender.Male, text: maleLength > 0 ? new string('m', maleLength) : null);
        var female = Range(12, 15, gender: Gender.Female, text: femaleLength > 0 ? new string('f', femaleLength) : null);

        var text = ReferenceRangeSelector.Select([male, female], Gender.Other, 20_000, decimals: 1)!.Text!;

        text.Length.ShouldBe(500);
        text.ShouldStartWith(maleLength > 0 ? "Male mmm" : "Male 13.0 - 17.0; ");
        text.ShouldContain("; Female ");
        text.ShouldEndWith(femaleLength > 0 ? "f…" : "; Female 12.0 - 15.0");
        if (maleLength > 0)
        {
            text.ShouldContain("m…; Female ");
        }
    }

    [Fact]
    public void Both_intervals_are_read_only_when_both_apply_at_that_age()
    {
        var adultMale = Range(13, 17, gender: Gender.Male, from: Years(18));
        var female = Range(12, 15, gender: Gender.Female);

        ReferenceRangeSelector.Select([adultMale, female], Gender.Other, Years(10)).ShouldBeNull();
        ReferenceRangeSelector.ReadsBothSexes([adultMale, female], Gender.Other, Years(10)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(Gender.Male, 13.0, 17.0)]
    [InlineData(Gender.Female, 12.0, 15.0)]
    public void Male_and_female_patients_keep_their_own_interval(Gender gender, double low, double high)
    {
        var range = ReferenceRangeSelector.Select(SeededRanges("HB"), gender, Years(30), decimals: 1)!;

        range.Low.ShouldBe((decimal)low);
        range.High.ShouldBe((decimal)high);
        range.Text.ShouldBeNull();
        ReferenceRangeSelector.ReadsBothSexes(SeededRanges("HB"), gender, Years(30)).ShouldBeFalse();
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
