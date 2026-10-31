using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

public sealed class AutoValidationPolicyTests
{
    private static readonly AutoValidationInput Clean = new(
        LabResultType.Numeric, ResultFlag.Normal, TestAllowsAutoValidation: true, InstrumentAllowsAutoValidation: true, QcAcceptedToday: true,
        new DeltaOutcome(4.0m, TestData.Now.AddDays(-1), 5.0m, false), InstrumentFlags: null);

    [Fact]
    public void An_in_range_result_with_accepted_QC_and_a_passed_delta_is_auto_validated()
    {
        var decision = AutoValidationPolicy.Evaluate(Clean);

        decision.Approved.ShouldBeTrue();
        decision.Reasons.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ResultFlag.Low)]
    [InlineData(ResultFlag.High)]
    [InlineData(ResultFlag.CriticalLow)]
    [InlineData(ResultFlag.CriticalHigh)]
    [InlineData(ResultFlag.Abnormal)]
    public void Abnormal_and_critical_results_always_wait_for_a_pathologist(ResultFlag flag) =>
        AutoValidationPolicy.Evaluate(Clean with { Flag = flag }).Approved.ShouldBeFalse();

    [Fact]
    public void A_result_without_an_applicable_interval_is_not_auto_validated()
    {
        var decision = AutoValidationPolicy.Evaluate(Clean with { Flag = null });

        decision.Approved.ShouldBeFalse();
        decision.Reasons.ShouldContain("No reference interval applies to this patient.");
    }

    [Fact]
    public void A_result_read_against_both_the_male_and_the_female_interval_is_verified_by_a_person()
    {
        var decision = AutoValidationPolicy.Evaluate(Clean with { ReadAgainstBothSexes = true });

        decision.Approved.ShouldBeFalse();
        decision.Reasons.ShouldContain(
            "The patient is recorded as neither male nor female, so the result was read against both the male and the female interval.");
    }

    [Fact]
    public void Missing_or_failed_QC_blocks_auto_validation() =>
        AutoValidationPolicy.Evaluate(Clean with { QcAcceptedToday = false }).Approved.ShouldBeFalse();

    [Fact]
    public void A_failed_delta_check_blocks_auto_validation() =>
        AutoValidationPolicy.Evaluate(Clean with { Delta = new DeltaOutcome(4.0m, TestData.Now, 60m, true) }).Approved.ShouldBeFalse();

    [Theory]
    [InlineData("N", true)]
    [InlineData("n", true)]
    [InlineData("", true)]
    [InlineData("H", false)]
    [InlineData("A", false)]
    [InlineData(">", false)]
    public void Only_benign_analyser_flags_allow_auto_validation(string flags, bool approved) =>
        AutoValidationPolicy.Evaluate(Clean with { InstrumentFlags = flags }).Approved.ShouldBe(approved);

    [Fact]
    public void Test_and_analyser_switches_and_non_numeric_results_block_auto_validation()
    {
        AutoValidationPolicy.Evaluate(Clean with { TestAllowsAutoValidation = false }).Approved.ShouldBeFalse();
        AutoValidationPolicy.Evaluate(Clean with { InstrumentAllowsAutoValidation = false }).Approved.ShouldBeFalse();
        AutoValidationPolicy.Evaluate(Clean with { ResultType = LabResultType.Options }).Approved.ShouldBeFalse();
    }

    [Fact]
    public void Every_failed_condition_is_explained()
    {
        var decision = AutoValidationPolicy.Evaluate(Clean with { Flag = ResultFlag.High, QcAcceptedToday = false, InstrumentFlags = "H" });

        decision.Reasons.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData("P")]
    [InlineData("S")]
    [InlineData("C")]
    [InlineData("R")]
    public void A_result_the_analyser_did_not_report_as_final_waits_for_a_person(string status)
    {
        var decision = AutoValidationPolicy.Evaluate(Clean with { InstrumentStatus = status });

        decision.Approved.ShouldBeFalse();
        decision.Reasons.ShouldBe([$"The analyser did not report the result as final (status {status})."]);
    }

    [Theory]
    [InlineData("F")]
    [InlineData("f")]
    [InlineData(null)]
    [InlineData("")]
    public void A_final_result_or_one_sent_without_a_status_may_be_auto_validated(string? status) =>
        AutoValidationPolicy.Evaluate(Clean with { InstrumentStatus = status }).Approved.ShouldBeTrue();

    [Fact]
    public void A_value_sent_in_another_unit_than_the_tests_waits_for_a_person()
    {
        // A glucose in mmol/L read against an interval in mg/dL: 5.4 would be flagged low, or pass in another analyte.
        var decision = AutoValidationPolicy.Evaluate(Clean with { InstrumentUnit = "mmol/L", TestUnit = "mg/dL" });

        decision.Approved.ShouldBeFalse();
        decision.Reasons.ShouldBe(["The analyser sent the value in mmol/L, not in the test's unit, mg/dL."]);
        AutoValidationPolicy.Evaluate(Clean with { InstrumentUnit = "%", TestUnit = null }).Reasons
            .ShouldBe(["The analyser sent the value in %, and the test has no unit to compare it with."]);
    }

    [Theory]
    [InlineData("mg/dL", "mg/dl")]
    [InlineData("10*3/uL", "10*3/µL")]
    [InlineData("µIU/mL", "μIU/mL")]
    [InlineData(" g/dL ", "g / dL")]
    [InlineData(null, "mmol/L")]
    [InlineData("", "mmol/L")]
    public void The_same_unit_written_another_way_or_no_unit_from_the_analyser_is_not_a_mismatch(string? analyser, string? test) =>
        AutoValidationPolicy.Evaluate(Clean with { InstrumentUnit = analyser, TestUnit = test }).Approved.ShouldBeTrue();
}
