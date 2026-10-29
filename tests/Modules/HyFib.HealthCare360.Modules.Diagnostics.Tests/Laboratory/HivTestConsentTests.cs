using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// Which tests are HIV tests, held until consent is recorded (HIV and AIDS Act 2017, s.5), and the consent the lab
/// records on one.
/// </summary>
public sealed class HivTestConsentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("LAB-HIV", "HIV 1 & 2 antibodies with p24 antigen (4th generation)", null)]
    [InlineData("SER-21", "Anti-HIV 1 and 2", null)]
    [InlineData("MOL-07", "HIV-1 RNA viral load", null)]
    [InlineData("RAP-3", "Rapid card test (HIV1/2)", null)]
    [InlineData("SER-22", "Retroviral screen", "56888-1")]
    [InlineData("SER-23", "Western blot", "5221-7")]
    public void An_hiv_test_is_known_by_its_code_name_or_loinc(string code, string name, string? loinc) =>
        HivTestConsent.IsHiv(Test(code, name, loinc)).ShouldBeTrue();

    [Theory]
    [InlineData("LAB-CD4", "CD4 count", null)]
    [InlineData("LAB-HBSAG", "Hepatitis B surface antigen", null)]
    [InlineData("SER-30", "Archive sample", null)]
    [InlineData("LAB-CBC", "Complete blood count (CBC)", "58410-2")]
    public void Other_tests_are_not(string code, string name, string? loinc) =>
        HivTestConsent.IsHiv(Test(code, name, loinc)).ShouldBeFalse();

    [Fact]
    public void A_panel_with_an_hiv_analyte_needs_consent_and_one_without_does_not()
    {
        var hbsag = Test("LAB-HBSAG", "Hepatitis B surface antigen", null);
        var hiv = Test("LAB-HIV", "HIV 1 & 2 antibodies", null);
        HivTestConsent.Needed(Panel("PRE-OP", "Pre-operative profile", hbsag, hiv), Master(hbsag, hiv)).ShouldBeTrue();
        HivTestConsent.Needed(Panel("PRE-OP", "Pre-operative profile", hbsag), Master(hbsag, hiv)).ShouldBeFalse();
    }

    [Fact]
    public void An_hiv_analyte_in_a_panel_within_a_panel_needs_consent_down_to_the_depth_the_laboratory_expands()
    {
        var hbsag = Test("LAB-HBSAG", "Hepatitis B surface antigen", null);
        var hiv = Test("LAB-HIV", "HIV 1 & 2 antibodies", null);
        var viral = Panel("VIRAL", "Viral markers", hbsag, hiv);
        var preOp = Panel("PRE-OP", "Pre-operative profile", Test("LAB-CBC", "Complete blood count", null), viral);
        HivTestConsent.Needed(preOp, Master(viral, hbsag, hiv)).ShouldBeTrue();

        // Three panels deep is as far as the laboratory expands, and as far as the rule looks.
        var cardiac = Panel("CARDIAC", "Cardiac work-up", preOp);
        HivTestConsent.Needed(cardiac, Master(preOp, viral, hbsag, hiv)).ShouldBeTrue();
        HivTestConsent.Needed(Panel("ANNUAL", "Annual check", cardiac), Master(cardiac, preOp, viral, hbsag, hiv)).ShouldBeFalse();
    }

    [Fact]
    public void The_patients_consent_is_recorded_once_and_stands_for_a_repeat_sample()
    {
        var item = new LabOrderItem { TestName = "HIV 1 & 2 antibodies" };
        var counsellor = Guid.NewGuid();

        item.RecordConsent(ConsentBasis.Patient, "ignored", "ignored", " ICTC/42 ", counsellor, Now).IsSuccess.ShouldBeTrue();
        (item.ConsentBasis, item.ConsentGivenBy, item.ConsentRelation, item.ConsentReference, item.ConsentRecordedBy, item.ConsentRecordedAt)
            .ShouldBe((ConsentBasis.Patient, (string?)null, (string?)null, "ICTC/42", counsellor, Now));

        // Recorded again (a repeat sample), it is not overwritten.
        item.RecordConsent(ConsentBasis.CourtOrder, null, null, "CRL 12/2026", null, Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        (item.ConsentBasis, item.ConsentRecordedAt).ShouldBe((ConsentBasis.Patient, Now));
    }

    [Fact]
    public void A_representative_is_named_with_the_relation_and_a_court_order_by_its_number()
    {
        new LabOrderItem { TestName = "HIV" }.RecordConsent(ConsentBasis.Representative, "Lakshmi", null, null, null, Now)
            .Error!.Fields!.Keys.ShouldBe(["givenBy"]);
        new LabOrderItem { TestName = "HIV" }.RecordConsent(ConsentBasis.CourtOrder, null, null, "  ", null, Now)
            .Error!.Fields!.Keys.ShouldBe(["reference"]);

        var child = new LabOrderItem { TestName = "HIV" };
        child.RecordConsent(ConsentBasis.Representative, " Lakshmi ", " Mother ", null, null, Now).IsSuccess.ShouldBeTrue();
        (child.ConsentGivenBy, child.ConsentRelation, child.ConsentReference).ShouldBe(("Lakshmi", "Mother", (string?)null));
    }

    private static LabTest Test(string code, string name, string? loinc) => new() { Code = code, Name = name, Loinc = loinc };

    private static LabTest Panel(string code, string name, params LabTest[] components) => new()
    {
        Code = code,
        Name = name,
        IsPanel = true,
        Components = [.. components.Select(c => new LabTestComponent { TestId = c.Id })],
    };

    private static Dictionary<Guid, LabTest> Master(params LabTest[] tests) => tests.ToDictionary(t => t.Id);
}
