using System.Globalization;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Seed;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// The results HC360 calculates from others on the same order and sample (N7: "Some parameters are not run, which means
/// calculated: indirect bilirubin, globulin, A/G ratio, VLDL"), with worked examples: the value, rounded to the test's
/// decimals, the note the verifier reads, and when a value measured, typed in, or not calculable stands instead.
/// </summary>
public sealed class CalculatedResultsTests
{
    private readonly Guid _order = Guid.CreateVersion7();
    private readonly Guid _item = Guid.CreateVersion7();
    private readonly Guid _sample = Guid.CreateVersion7();

    [Fact]
    public void Indirect_bilirubin_is_the_total_less_the_direct()
    {
        var tube = Lft(("TBIL", "1.20"), ("DBIL", "0.30"));

        Fill(tube, "IBIL").ShouldBe(new CalculationStep.Fill(
            "0.90", "Calculated by HC360: Bilirubin, total − Bilirubin, direct = 1.20 − 0.30 = 0.90 mg/dL.", Reopen: false));
    }

    [Fact]
    public void Globulin_is_the_total_protein_less_the_albumin_and_the_ag_ratio_the_albumin_over_the_globulin()
    {
        var tube = Lft(("TP", "7.2"), ("ALB", "4.1"));

        var globulin = Fill(tube, "GLOB").ShouldBeOfType<CalculationStep.Fill>();
        globulin.ValueText.ShouldBe("3.1");
        globulin.Note.ShouldBe("Calculated by HC360: Total protein − Albumin = 7.2 − 4.1 = 3.1 g/dL.");

        // The A/G ratio reads the globulin just calculated: 4.1 ÷ 3.1 = 1.3226, to the test's two decimals.
        Apply(tube, "GLOB", globulin);
        var ratio = Fill(tube, "AGR").ShouldBeOfType<CalculationStep.Fill>();
        ratio.ValueText.ShouldBe("1.32");
        ratio.Note.ShouldBe("Calculated by HC360: Albumin ÷ Globulin = 4.1 ÷ 3.1 = 1.32.");
    }

    [Theory]
    [InlineData("3.5", "2.0", "1.75")]
    [InlineData("4.5", "2.7", "1.67")]
    [InlineData("4.0", "3.0", "1.33")]
    public void The_ag_ratio_is_rounded_half_away_from_zero_to_the_tests_decimals(string albumin, string globulin, string ratio)
    {
        var tube = Lft(("ALB", albumin), ("GLOB", globulin));

        Fill(tube, "AGR").ShouldBeOfType<CalculationStep.Fill>().ValueText.ShouldBe(ratio);
    }

    [Theory]
    [InlineData("150", "30")]
    [InlineData("97", "19")]
    [InlineData("399", "80")]
    public void Vldl_is_a_fifth_of_the_triglycerides_below_400_mg_per_dl(string triglycerides, string vldl)
    {
        var tube = Lipid(("TG", triglycerides));

        var step = Fill(tube, "VLDL").ShouldBeOfType<CalculationStep.Fill>();
        step.ValueText.ShouldBe(vldl);
        step.Note.ShouldBe($"Calculated by HC360: Triglycerides ÷ 5 (Friedewald) = {triglycerides} ÷ 5 = {vldl} mg/dL.");
    }

    [Theory]
    [InlineData("400")]
    [InlineData("652")]
    public void From_400_mg_per_dl_of_triglycerides_up_vldl_and_ldl_are_left_for_a_person(string triglycerides)
    {
        var tube = Lipid(("CHOL", "240"), ("HDL", "38"), ("TG", triglycerides));

        Fill(tube, "VLDL").ShouldBe(new CalculationStep.NotCalculated(
            $"Not calculated: The triglycerides are {triglycerides} mg/dL: from 400 up, VLDL is not calculated (Friedewald). Measure it, or enter it by hand.", Reopen: false));
        Fill(tube, "LDL").ShouldBeOfType<CalculationStep.NotCalculated>().Why.ShouldContain("LDL is not calculated (Friedewald)");
    }

    [Fact]
    public void Ldl_is_the_cholesterol_less_the_hdl_and_a_fifth_of_the_triglycerides()
    {
        var tube = Lipid(("CHOL", "200"), ("HDL", "45"), ("TG", "150"));

        var step = Fill(tube, "LDL").ShouldBeOfType<CalculationStep.Fill>();
        step.ValueText.ShouldBe("125");
        step.Note.ShouldBe("Calculated by HC360: Total cholesterol − HDL − Triglycerides ÷ 5 (Friedewald) = 200 − 45 − 150 ÷ 5 = 125 mg/dL.");
    }

    [Fact]
    public void Nothing_is_calculated_until_every_input_has_a_value()
    {
        var tube = Lft(("TBIL", "1.20"));

        Fill(tube, "IBIL").ShouldBe(CalculationStep.Nothing);
        Fill(tube, "GLOB").ShouldBe(CalculationStep.Nothing);
    }

    [Fact]
    public void A_value_typed_in_or_measured_for_it_stands()
    {
        var typed = Lft(("TBIL", "1.20"), ("DBIL", "0.30"), ("IBIL", "0.95"));
        Fill(typed, "IBIL").ShouldBe(CalculationStep.Nothing, "a person entered it");

        var measured = Lipid(("TG", "150"), ("VLDL", "27"));
        measured.Single(r => r.TestCode == "VLDL").InstrumentCode = "C150";
        Fill(measured, "VLDL").ShouldBe(CalculationStep.Nothing, "the analyser measured it");
    }

    [Fact]
    public void A_changed_input_calculates_it_again_before_it_is_verified()
    {
        var tube = Lft(("TBIL", "1.20"), ("DBIL", "0.30"));
        Apply(tube, "IBIL", Fill(tube, "IBIL"));

        Fill(tube, "IBIL").ShouldBe(CalculationStep.Nothing, "nothing changed");
        Set(tube, "TBIL", "1.50");

        Fill(tube, "IBIL").ShouldBe(new CalculationStep.Fill(
            "1.20", "Calculated by HC360: Bilirubin, total − Bilirubin, direct = 1.50 − 0.30 = 1.20 mg/dL.", Reopen: false));
    }

    [Fact]
    public void An_input_corrected_after_it_was_verified_takes_it_back_to_unverified()
    {
        var tube = Lft(("TP", "7.2"), ("ALB", "4.1"));
        Apply(tube, "GLOB", Fill(tube, "GLOB"));
        tube.Single(r => r.TestCode == "GLOB").Verify(TestData.UserId, TestData.Now, automatic: false).IsSuccess.ShouldBeTrue();

        Fill(tube, "GLOB").ShouldBe(CalculationStep.Nothing, "its inputs are as they were");
        Set(tube, "TP", "6.2");

        var step = Fill(tube, "GLOB").ShouldBeOfType<CalculationStep.Fill>();
        (step.ValueText, step.Reopen).ShouldBe(("2.1", true));
    }

    [Fact]
    public void An_input_whose_value_went_takes_the_calculated_value_along()
    {
        var tube = Lft(("TBIL", "1.20"), ("DBIL", "0.30"));
        Apply(tube, "IBIL", Fill(tube, "IBIL"));

        // The analyser that sent the direct bilirubin withdrew it: the test is pending again.
        var direct = tube.Single(r => r.TestCode == "DBIL");
        direct.InstrumentCode = "C150";
        direct.Withdraw("C150", "C150 withdrew its result.").ShouldBeTrue();

        Fill(tube, "IBIL").ShouldBe(new CalculationStep.NotCalculated("Not calculated: Bilirubin, direct (conjugated) has no value now.", Reopen: false));
    }

    [Theory]
    [InlineData("<0.10", "Bilirubin, total is <0.10, beyond what the analyser measures: it cannot be calculated from that.")]
    [InlineData(">30.00", "Bilirubin, total is >30.00, beyond what the analyser measures: it cannot be calculated from that.")]
    public void A_value_beyond_the_measuring_range_is_not_calculated_from(string total, string why)
    {
        var tube = Lft(("TBIL", total), ("DBIL", "0.05"));

        Fill(tube, "IBIL").ShouldBe(new CalculationStep.NotCalculated("Not calculated: " + why, Reopen: false));
    }

    [Fact]
    public void An_input_in_another_unit_than_the_formula_is_written_for_is_not_calculated_from()
    {
        var tube = Lft(("TBIL", "20.5"), ("DBIL", "5.1"));
        tube.Single(r => r.TestCode == "TBIL").Unit = "µmol/L";

        Fill(tube, "IBIL").ShouldBe(new CalculationStep.NotCalculated(
            "Not calculated: Bilirubin, total is in µmol/L; the formula is written for mg/dL.", Reopen: false));
    }

    [Fact]
    public void A_value_that_cannot_be_is_left_for_a_person_and_said_once()
    {
        var tube = Lft(("TBIL", "0.40"), ("DBIL", "0.60"));

        var step = Fill(tube, "IBIL").ShouldBeOfType<CalculationStep.NotCalculated>();
        step.Why.ShouldBe("Not calculated: The direct bilirubin is above the total: check both.");
        tube.Single(r => r.TestCode == "IBIL").LeaveUncalculated(step.Why);
        Fill(tube, "IBIL").ShouldBe(CalculationStep.Nothing, "it says so already");
    }

    [Fact]
    public void Taking_a_calculated_value_back_leaves_the_test_pending_with_why()
    {
        var tube = Lft(("TBIL", "1.20"), ("DBIL", "0.30"));
        var indirect = tube.Single(r => r.TestCode == "IBIL");
        Apply(tube, "IBIL", Fill(tube, "IBIL"));
        indirect.Verify(TestData.UserId, TestData.Now, automatic: false);

        indirect.LeaveUncalculated("Not calculated: Bilirubin, direct (conjugated) has no value now.");

        (indirect.Status, indirect.ValueText, indirect.NumericValue, indirect.VerifiedAt, indirect.EnteredBy).ShouldBe((LabResultStatus.Pending, null, null, null, null));
        indirect.HeldReasons.ShouldBe(["Not calculated: Bilirubin, direct (conjugated) has no value now."]);
        CalculatedResults.IsCalculated(indirect).ShouldBeFalse();
    }

    [Fact]
    public void A_calculated_result_is_known_by_its_note_and_loses_it_when_a_person_or_an_analyser_enters_a_value()
    {
        var tube = Lft(("TBIL", "1.20"), ("DBIL", "0.30"));
        var indirect = tube.Single(r => r.TestCode == "IBIL");
        Apply(tube, "IBIL", Fill(tube, "IBIL"));
        CalculatedResults.IsCalculated(indirect).ShouldBeTrue();

        indirect.Record(TestData.Capture("0.95", 0.95m), TestData.UserId, TestData.Now);

        CalculatedResults.IsCalculated(indirect).ShouldBeFalse("a value typed in replaces the calculation, and its note");
    }

    [Fact]
    public void A_result_another_reads_is_calculated_first()
    {
        var tube = Lft(("TP", "7.2"), ("ALB", "4.1"));

        ResultFormulas.InOrder(tube).Select(r => r.TestCode).ShouldBe(["IBIL", "GLOB", "AGR"]);
    }

    [Fact]
    public void Every_formula_fills_a_seeded_analyte_from_seeded_analytes_in_their_units()
    {
        var analytes = LabCatalog.Analytes.ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var formula in ResultFormulas.ByCode.Values)
        {
            var filled = analytes[formula.Code];
            filled.Result.Unit.ShouldBe(formula.Unit, $"{formula.Code} is printed in the unit its formula gives");
            foreach (var input in formula.Inputs)
            {
                analytes[input.Code].Result.Unit.ShouldBe(input.Unit, $"{formula.Code} reads {input.Code} in {input.Unit}");
            }
        }

        ResultFormulas.ByCode.Keys.ShouldBe(["IBIL", "GLOB", "AGR", "VLDL", "LDL"], ignoreOrder: true);
    }

    /// <summary>The seeded LFT's analytes on one tube, the given ones entered by hand; IBIL, GLOB and AGR pending.</summary>
    private List<LabResult> Lft(params (string Code, string Value)[] entered) =>
        Tube(["TBIL", "DBIL", "IBIL", "TP", "ALB", "GLOB", "AGR"], entered);

    private List<LabResult> Lipid(params (string Code, string Value)[] entered) => Tube(["CHOL", "TG", "HDL", "LDL", "VLDL"], entered);

    private List<LabResult> Tube(string[] codes, (string Code, string Value)[] entered)
    {
        var results = codes.Select((code, i) =>
        {
            var seed = LabCatalog.Analytes.Single(a => a.Code == code);
            return new LabResult
            {
                LabOrderId = _order,
                LabOrderItemId = _item,
                SampleId = _sample,
                LabTestId = Guid.CreateVersion7(),
                PatientId = Guid.CreateVersion7(),
                TestCode = code,
                TestName = seed.Name,
                Unit = seed.Result.Unit,
                ResultType = LabResultType.Numeric,
                DisplayOrder = i + 1,
                Status = LabResultStatus.Pending,
            };
        }).ToList();
        foreach (var (code, value) in entered)
        {
            Set(results, code, value);
        }

        return results;
    }

    /// <summary>A value entered by hand (or corrected) on the tube's result of <paramref name="code"/>.</summary>
    private static void Set(List<LabResult> tube, string code, string value)
    {
        var result = tube.Single(r => r.TestCode == code);
        result.ValueText = value;
        result.NumericValue = decimal.Parse(value.TrimStart('<', '>'), CultureInfo.InvariantCulture);
        result.Status = LabResultStatus.Entered;
        result.EnteredBy = TestData.UserId;
        result.HeldReasons = [];
    }

    /// <summary>What the calculation step plans for <paramref name="code"/>, with the seeded test's decimals.</summary>
    private static CalculationStep Fill(List<LabResult> tube, string code) =>
        CalculatedResults.Plan(tube.Single(r => r.TestCode == code), ResultFormulas.For(code)!, tube, LabCatalog.Analytes.Single(a => a.Code == code).Result.Decimals);

    /// <summary>Applies a calculation as the calculator does: the value, entered by nobody, with its note.</summary>
    private static void Apply(List<LabResult> tube, string code, CalculationStep step)
    {
        var fill = step.ShouldBeOfType<CalculationStep.Fill>();
        var result = tube.Single(r => r.TestCode == code);
        if (fill.Reopen)
        {
            result.Reopen();
        }

        var numeric = decimal.Parse(fill.ValueText, CultureInfo.InvariantCulture);
        result.Record(TestData.Capture(fill.ValueText, numeric), null, TestData.Now).IsSuccess.ShouldBeTrue();
        result.HoldForReview([fill.Note]);
    }
}
