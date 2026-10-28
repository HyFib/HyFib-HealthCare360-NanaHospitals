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

    [Theory]
    [InlineData("A15.0")]
    [InlineData("A16.2")]
    [InlineData("A17.0")]
    [InlineData("a18.0")]
    [InlineData("A19.9")]
    public void Tuberculosis_is_caught_by_its_diagnosis_for_Nikshay_and_is_not_on_the_IHIP_list(string code)
    {
        var tb = IdspDiseases.ForIcd(code).ShouldNotBeNull();
        (tb.Code, tb.Nikshay, tb.Immediate, tb.EpisodeDays).ShouldBe((IdspDiseases.TuberculosisCode, true, false, 365));
        IdspDiseases.All.ShouldNotContain(tb, "the IHIP forms do not carry tuberculosis");
        IdspDiseases.Find("TB").ShouldBeSameAs(tb);
    }

    [Fact]
    public void Tuberculosis_is_confirmed_by_Mycobacterium_tuberculosis_grown_and_not_by_other_mycobacteria()
    {
        IdspDiseases.ForLabResult("LAB-AFB-CS", null, "Mycobacterium tuberculosis complex")!.Code.ShouldBe("TB");
        IdspDiseases.ForLabResult("LAB-AFB-CS", null, "Mycobacterium avium complex").ShouldBeNull();
    }

    [Fact]
    public void A_tuberculosis_case_is_notified_under_its_Nikshay_id_on_a_day_from_its_diagnosis_to_today()
    {
        var today = new DateOnly(2026, 10, 28);
        var tb = new NotifiableCase { DiseaseCode = "TB" };
        tb.Presume(Guid.NewGuid(), "A15.0", Monday);

        tb.RecordNikshay("10-23", today, today, null, Monday).Error!.Fields!.Keys.ShouldBe(["nikshayId"]);
        tb.RecordNikshay("10234567", today.AddDays(1), today, null, Monday).Error!.Fields!.Keys.ShouldBe(["notifiedOn"]);
        tb.RecordNikshay("10234567", new DateOnly(2026, 10, 18), today, null, Monday).Error!.Fields!.Keys.ShouldBe(["notifiedOn"]);

        var by = Guid.NewGuid();
        tb.RecordNikshay(" 10234567 ", new DateOnly(2026, 10, 19), today, by, Monday.AddDays(2)).IsSuccess.ShouldBeTrue();
        (tb.NikshayId, tb.NikshayNotifiedOn, tb.NikshayRecordedBy).ShouldBe(("10234567", new DateOnly(2026, 10, 19), by));

        // Only tuberculosis goes to Ni-kshay, and a discarded case is not notified.
        var dengue = new NotifiableCase { DiseaseCode = "DENGUE" };
        dengue.Presume(null, "A90", Monday);
        dengue.RecordNikshay("10234567", today, today, null, Monday).Error!.Code.ShouldBe("idsp.not_nikshay");
        var ruledOut = new NotifiableCase { DiseaseCode = "TB" };
        ruledOut.Presume(null, "A16.2", Monday);
        ruledOut.Discard("Sputum CBNAAT negative, another diagnosis", null, Monday);
        ruledOut.RecordNikshay("10234567", today, today, null, Monday).Error!.Code.ShouldBe("idsp.case_discarded");
    }

    [Fact]
    public void Every_disease_has_a_unique_code_and_the_outbreak_prone_ones_are_marked()
    {
        IdspDiseases.Notifiable.Select(d => d.Code).ShouldBeUnique();
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
