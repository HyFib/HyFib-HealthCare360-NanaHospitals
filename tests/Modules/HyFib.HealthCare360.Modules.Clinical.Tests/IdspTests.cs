using HyFib.HealthCare360.Modules.Clinical.Domain.Idsp;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>Which codes and tests are notifiable under IDSP, the reporting week, and one patient's case on the P and L forms.</summary>
public sealed class IdspTests
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("A01.0", "ENTERIC_FEVER")]
    [InlineData("a00.9", "CHOLERA")]
    [InlineData("A90", "DENGUE")]
    [InlineData("A92.0", "CHIKUNGUNYA")]
    [InlineData("B50.9", "MALARIA")]
    [InlineData("J18.9", "PNEUMONIA")]
    public void A_code_or_a_code_under_it_is_notified_as_its_disease(string code, string disease) =>
        IdspDiseases.ForIcd(code)!.Code.ShouldBe(disease);

    [Theory]
    [InlineData("A92.1")]
    [InlineData("A010")]
    [InlineData("B18.1")]
    [InlineData("Z21")]
    [InlineData("A15.0")]
    [InlineData(null)]
    public void Codes_outside_the_list_are_not_notified(string? code) => IdspDiseases.ForIcd(code).ShouldBeNull();

    [Fact]
    public void A_test_confirms_by_its_code_its_loinc_or_the_organism_it_grew()
    {
        IdspDiseases.ForLabResult("LAB-DENGUE-NS1", null, null)!.Code.ShouldBe("DENGUE");
        IdspDiseases.ForLabResult("HOSP-NS1", "75377-2", null)!.Code.ShouldBe("DENGUE");
        IdspDiseases.ForLabResult("WIDAL-TO", null, null)!.Code.ShouldBe("ENTERIC_FEVER");
        IdspDiseases.ForLabResult("LAB-STOOL-CS", null, "Vibrio cholerae O1, Ogawa")!.Code.ShouldBe("CHOLERA");
        IdspDiseases.ForLabResult("LAB-BLOOD-CS", null, "Escherichia coli").ShouldBeNull();
        IdspDiseases.ForLabResult("HB", null, null).ShouldBeNull();
    }

    [Fact]
    public void Every_disease_has_a_unique_code_and_the_outbreak_prone_ones_are_marked()
    {
        IdspDiseases.All.Select(d => d.Code).ShouldBeUnique();
        IdspDiseases.All.Where(d => d.Immediate).Select(d => d.Code).ShouldContain("CHOLERA");
        IdspDiseases.All.Where(d => d.Immediate).Select(d => d.Code).ShouldNotContain("ADD");
    }

    [Fact]
    public void The_week_runs_monday_to_sunday_in_india_and_is_due_the_monday_after()
    {
        var week = IdspWeek.Of(new DateOnly(2026, 10, 25));
        (week.Year, week.Week, week.Key).ShouldBe((2026, 43, 202643));
        (week.Monday, week.Sunday, week.DueOn).ShouldBe((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26)));
        week.Previous.ShouldBe(new IdspWeek(2026, 42));

        // Half past midnight on Monday in India is still Sunday in UTC.
        IdspWeek.Of(new DateTimeOffset(2026, 10, 25, 19, 0, 0, TimeSpan.Zero)).ShouldBe(new IdspWeek(2026, 44));

        // 1 January 2027 is a Friday: it belongs to the last week of 2026, which has 53.
        IdspWeek.Of(new DateOnly(2027, 1, 1)).ShouldBe(new IdspWeek(2026, 53));
        IdspWeek.IsValid(2026, 53).ShouldBeTrue();
        IdspWeek.IsValid(2027, 53).ShouldBeFalse();
    }

    [Fact]
    public void A_case_is_on_the_P_form_in_the_week_of_its_diagnosis_and_on_the_L_form_in_the_week_of_its_result()
    {
        var @case = new NotifiableCase { DiseaseCode = "DENGUE" };
        @case.Presume(Guid.NewGuid(), "A90", Monday);
        @case.Presume(Guid.NewGuid(), "A91", Monday.AddDays(1));
        @case.IcdCode.ShouldBe("A90", "a later diagnosis changes nothing");

        var result = Guid.NewGuid();
        @case.Confirm(result, "Dengue NS1 antigen", "Positive", Monday.AddDays(8));
        @case.IsOn(IdspForm.Presumptive, new IdspWeek(2026, 43)).ShouldBeTrue();
        @case.IsOn(IdspForm.Laboratory, new IdspWeek(2026, 43)).ShouldBeFalse();
        @case.IsOn(IdspForm.Laboratory, new IdspWeek(2026, 44)).ShouldBeTrue();
        @case.LastEventAt.ShouldBe(Monday.AddDays(8));

        // The result is amended to negative: the case leaves the L form and stays on the P form.
        @case.WithdrawConfirmation("Amended", Monday.AddDays(9));
        (@case.ConfirmedAt, @case.LabResultId, @case.Status).ShouldBe((null, null, NotifiableCaseStatus.Open));
        @case.IsOn(IdspForm.Presumptive, new IdspWeek(2026, 43)).ShouldBeTrue();
    }

    [Fact]
    public void A_case_opened_by_a_result_alone_is_discarded_when_the_result_is_amended_to_negative()
    {
        var @case = new NotifiableCase { DiseaseCode = "MALARIA" };
        @case.Confirm(Guid.NewGuid(), "Malaria antigen", "Pf positive", Monday);
        @case.WithdrawConfirmation("The confirming result was amended to negative.", Monday.AddHours(3));

        (@case.Status, @case.DiscardReason, @case.TestResult).ShouldBe(
            (NotifiableCaseStatus.Discarded, "The confirming result was amended to negative.", "Pf positive"));
        @case.IsOn(IdspForm.Laboratory, new IdspWeek(2026, 43)).ShouldBeFalse();
    }

    [Fact]
    public void A_discarded_case_needs_a_reason_and_leaves_both_forms()
    {
        var @case = new NotifiableCase { DiseaseCode = "CHOLERA" };
        @case.Presume(null, "A00.9", Monday);

        @case.Discard("  ", null, Monday).Error!.Fields!.Keys.ShouldBe(["reason"]);
        @case.Discard(" Ruled out ", null, Monday).IsSuccess.ShouldBeTrue();
        @case.DiscardReason.ShouldBe("Ruled out");
        @case.IsOn(IdspForm.Presumptive, new IdspWeek(2026, 43)).ShouldBeFalse();
        @case.Discard("Again", null, Monday).Error!.Code.ShouldBe("idsp.case_discarded");
    }
}
