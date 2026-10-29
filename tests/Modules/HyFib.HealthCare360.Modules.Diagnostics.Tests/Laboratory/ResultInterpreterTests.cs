using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

public sealed class ResultInterpreterTests
{
    private static readonly ReferenceRange Potassium = Range(3.5m, 5.1m, 2.5m, 6.5m);

    [Theory]
    [InlineData("4.2", ResultFlag.Normal)]
    [InlineData("3.5", ResultFlag.Normal)]
    [InlineData("5.1", ResultFlag.Normal)]
    [InlineData("3.4", ResultFlag.Low)]
    [InlineData("5.2", ResultFlag.High)]
    [InlineData("2.5", ResultFlag.Low)]
    [InlineData("2.4", ResultFlag.CriticalLow)]
    [InlineData("6.5", ResultFlag.High)]
    [InlineData("6.6", ResultFlag.CriticalHigh)]
    public void Numeric_values_are_flagged_with_exclusive_limits(string value, ResultFlag expected) =>
        ResultInterpreter.Interpret(Numeric(), Potassium, value).Value.Flag.ShouldBe(expected);

    [Theory]
    [InlineData("5.0", ResultFlag.CriticalLow)]
    [InlineData("11.0", ResultFlag.Low)]
    [InlineData("12.5", ResultFlag.Normal)]
    [InlineData("16.5", ResultFlag.Normal)]
    [InlineData("17.5", ResultFlag.High)]
    [InlineData("21.0", ResultFlag.CriticalHigh)]
    public void A_transgender_patients_haemoglobin_is_flagged_against_both_intervals(string value, ResultFlag expected)
    {
        var both = ReferenceRangeSelector.Select(SeededRanges("HB"), SharedKernel.Gender.Other, Years(30), decimals: 1);

        var result = ResultInterpreter.Interpret(Numeric(name: "Haemoglobin", unit: "g/dL"), both, value).Value;

        result.Flag.ShouldBe(expected);
        result.ReferenceText.ShouldBe("Male 13.0 - 17.0; Female 12.0 - 15.0");
    }

    [Theory]
    [InlineData("Negative", ResultFlag.Normal)]
    [InlineData("Trace", ResultFlag.Normal)]
    [InlineData("Positive", ResultFlag.Abnormal)]
    [InlineData("Strongly positive", ResultFlag.Critical)]
    public void A_transgender_patients_answer_is_abnormal_only_when_abnormal_for_both_sexes(string value, ResultFlag expected)
    {
        // As with a number: "Trace", normal for a woman, is not flagged; an answer critical for either sex is critical.
        var male = Answers(["Negative"], ["Strongly positive"], SharedKernel.Gender.Male);
        var female = Answers(["Negative", "Trace"], gender: SharedKernel.Gender.Female);
        var both = ReferenceRangeSelector.Select([male, female], SharedKernel.Gender.Other, Years(30));

        ResultInterpreter.Interpret(Options("Negative", "Trace", "Positive", "Strongly positive"), both, value).Value.Flag.ShouldBe(expected);
    }

    /// <summary>
    /// A range may hold critical limits or critical answers and nothing to print. Read against both, a critical result is
    /// still critical: reading both does not wait for a text to print.
    /// </summary>
    [Theory]
    [InlineData("5.0", ResultFlag.CriticalLow)]
    [InlineData("21.0", ResultFlag.CriticalHigh)]
    public void A_transgender_patients_result_is_critical_against_critical_limits_alone(string value, ResultFlag expected)
    {
        var male = Range(null, null, criticalLow: 7, criticalHigh: 20, gender: SharedKernel.Gender.Male);
        var female = Range(null, null, criticalLow: 6.5m, criticalHigh: 19, gender: SharedKernel.Gender.Female);
        var both = ReferenceRangeSelector.Select([male, female], SharedKernel.Gender.Other, Years(30), decimals: 1);

        ResultInterpreter.Interpret(Numeric(name: "Haemoglobin", unit: "g/dL"), both, value).Value.Flag.ShouldBe(expected);
    }

    [Fact]
    public void A_transgender_patients_answer_is_critical_against_critical_answers_alone()
    {
        var male = Answers([], ["Positive"], SharedKernel.Gender.Male);
        var female = Answers([], ["Positive"], SharedKernel.Gender.Female);
        var both = ReferenceRangeSelector.Select([male, female], SharedKernel.Gender.Other, Years(30));

        ResultInterpreter.Interpret(Options("Negative", "Positive"), both, "Positive").Value.Flag.ShouldBe(ResultFlag.Critical);
    }

    [Fact]
    public void An_answer_left_unflagged_for_one_sex_is_left_unflagged_for_a_transgender_patient()
    {
        var male = Answers([], gender: SharedKernel.Gender.Male, text: "See the pathologist's remarks");
        var female = Answers(["Negative"], gender: SharedKernel.Gender.Female);
        var both = ReferenceRangeSelector.Select([male, female], SharedKernel.Gender.Other, Years(30));

        ResultInterpreter.Interpret(Options("Negative", "Positive"), both, "Positive").Value.Flag.ShouldBeNull();
    }

    [Fact]
    public void Flag_uses_the_unrounded_value_while_the_report_shows_the_rounded_one()
    {
        var result = ResultInterpreter.Interpret(Numeric(decimals: 1), Potassium, "6.54").Value;

        result.ValueText.ShouldBe("6.5");
        result.Flag.ShouldBe(ResultFlag.CriticalHigh);
        result.NumericValue.ShouldBe(6.54m);
    }

    [Fact]
    public void Values_are_rounded_half_away_from_zero_to_the_test_precision()
    {
        ResultInterpreter.Interpret(Numeric(decimals: 1), Potassium, "4.25").Value.ValueText.ShouldBe("4.3");
        ResultInterpreter.Interpret(Numeric(decimals: 0), Range(150, 410), "249.5").Value.ValueText.ShouldBe("250");
        ResultInterpreter.Interpret(Numeric(decimals: 3), Range(1.005m, 1.030m), "1.015").Value.ValueText.ShouldBe("1.015");
    }

    [Fact]
    public void Indian_digit_grouping_and_unicode_comparators_are_accepted()
    {
        ResultInterpreter.Interpret(Numeric(decimals: 0), Range(150_000, 410_000), "1,20,000").Value.NumericValue.ShouldBe(120_000m);
        ResultInterpreter.Interpret(Numeric(decimals: 1), Range(null, 5.0m), "≤ 0.5").Value.ValueText.ShouldBe("<=0.5");
    }

    [Theory]
    [InlineData("<0.005", ResultFlag.Low)] // TSH below the analyser's measuring range
    [InlineData(">100", ResultFlag.High)]
    public void Analyser_comparators_are_flagged_against_limits(string value, ResultFlag expected) =>
        ResultInterpreter.Interpret(Numeric(decimals: 3), Range(0.27m, 4.2m), value).Value.Flag.ShouldBe(expected);

    [Fact]
    public void A_value_below_the_measuring_range_is_treated_as_possibly_critical()
    {
        ResultInterpreter.Interpret(Numeric(), Potassium, "<2.0").Value.Flag.ShouldBe(ResultFlag.CriticalLow);
        ResultInterpreter.Interpret(Numeric(decimals: 0), Range(70, 100, 50, 450), ">600").Value.Flag.ShouldBe(ResultFlag.CriticalHigh);
    }

    [Fact]
    public void A_bound_inside_an_upper_only_interval_is_normal_and_an_ambiguous_one_is_left_unflagged()
    {
        var crp = Range(null, 5.0m);

        ResultInterpreter.Interpret(Numeric(), crp, "<0.5").Value.Flag.ShouldBe(ResultFlag.Normal);
        ResultInterpreter.Interpret(Numeric(), crp, "<10").Value.Flag.ShouldBeNull();
        ResultInterpreter.Interpret(Numeric(), Range(40, null), ">30").Value.Flag.ShouldBeNull();
        ResultInterpreter.Interpret(Numeric(), Range(40, null), ">60").Value.Flag.ShouldBe(ResultFlag.Normal);
    }

    [Fact]
    public void Without_an_applicable_interval_the_value_is_recorded_but_not_flagged()
    {
        var result = ResultInterpreter.Interpret(Numeric(), null, "4.1").Value;

        result.Flag.ShouldBeNull();
        result.ReferenceText.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("high")]
    [InlineData("4..2")]
    public void Unreadable_numeric_values_are_rejected(string value)
    {
        var result = ResultInterpreter.Interpret(Numeric(), Potassium, value);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Fields!.ShouldContainKey("value");
    }

    [Fact]
    public void Options_match_case_insensitively_and_keep_the_master_spelling()
    {
        var test = Options("Negative", "Positive for P. vivax", "Positive for P. falciparum");
        var range = Answers(["Negative"], ["Positive for P. falciparum"]);

        ResultInterpreter.Interpret(test, range, "negative").Value.ShouldBe(new InterpretedResult("Negative", null, ResultFlag.Normal, "Negative"));
        ResultInterpreter.Interpret(test, range, "Positive for P. vivax").Value.Flag.ShouldBe(ResultFlag.Abnormal);
        ResultInterpreter.Interpret(test, range, "positive for p. falciparum").Value.Flag.ShouldBe(ResultFlag.Critical);
        ResultInterpreter.Interpret(test, range, "Maybe").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Culture_with_an_isolate_is_abnormal_and_no_growth_is_normal()
    {
        var culture = new LabTest { Code = "UC", Name = "Urine culture", ResultType = LabResultType.Culture };
        var range = Answers(["No growth", "Sterile"]);

        ResultInterpreter.Interpret(culture, range, "No growth").Value.Flag.ShouldBe(ResultFlag.Normal);
        ResultInterpreter.Interpret(culture, range, "Significant growth", "Escherichia coli").Value.Flag.ShouldBe(ResultFlag.Abnormal);
        ResultInterpreter.Interpret(culture, Answers(["No growth"], ["Salmonella Typhi"]), "Growth", "Salmonella Typhi").Value.Flag.ShouldBe(ResultFlag.Critical);
    }

    [Fact]
    public void Free_text_without_expected_answers_is_not_flagged()
    {
        var test = new LabTest { Code = "UR-PUS", Name = "Pus cells", ResultType = LabResultType.Text };

        ResultInterpreter.Interpret(test, Range(null, null, text: "0 - 5"), "2-4").Value.ShouldBe(new InterpretedResult("2-4", null, null, "0 - 5"));
    }

    [Fact]
    public void Reference_text_is_generated_from_the_interval()
    {
        ResultInterpreter.ReferenceText(Range(13m, 17m), 1).ShouldBe("13.0 - 17.0");
        ResultInterpreter.ReferenceText(Range(null, 40), 0).ShouldBe("Up to 40");
        ResultInterpreter.ReferenceText(Range(40, null), 0).ShouldBe("40 and above");
        ResultInterpreter.ReferenceText(Range(70, 100, text: "70 - 100 (normal)"), 0).ShouldBe("70 - 100 (normal)");
        ResultInterpreter.ReferenceText(Answers(["Negative", "Nil"]), 0).ShouldBe("Negative / Nil");
        ResultInterpreter.ReferenceText(null, 1).ShouldBeNull();
    }
}
