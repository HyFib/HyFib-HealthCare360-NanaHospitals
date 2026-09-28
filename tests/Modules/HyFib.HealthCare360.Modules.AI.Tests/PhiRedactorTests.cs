using HyFib.HealthCare360.Modules.AI.Services.Privacy;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class PhiRedactorTests
{
    private static readonly RedactionContext Patient = new(["Ravi Kumar Sharma", "Mrs. Priya Sharma"], ["NH26000123", "IP-2026-0042"]);

    [Fact]
    public void Replaces_each_kind_of_direct_identifier_with_a_categorised_placeholder()
    {
        var result = PhiRedactor.Redact(
            "Pt UHID: NH26000123, mobile +91 98765 43210, email ravi.kumar@example.com, ABHA 91-1234-5678-9012, Aadhaar 2345 6789 0123, landline 044-24567890.");

        result.Text.ShouldBe("Pt UHID: [UHID_1], mobile [PHONE_1], email [EMAIL_1], ABHA [ABHA_1], Aadhaar [AADHAAR_1], landline [PHONE_2].");
        result.Counts["PHONE"].ShouldBe(2);
        result.Total.ShouldBe(6);
        result.Placeholders["[UHID_1]"].ShouldBe("NH26000123");
        result.Placeholders["[PHONE_1]"].ShouldBe("+91 98765 43210");
    }

    [Theory]
    [InlineData("Review NH26000123 today", "Review [UHID_1] today")]
    [InlineData("patient id nh 2600 0123 seen", "patient id [UHID_1] seen")]
    [InlineData("ABHA address ravi.k@abdm linked", "ABHA address [ABHA_1] linked")]
    [InlineData("Old record 123456789012345 found", "Old record [ID_1] found")]
    public void Recognises_hospital_and_national_identifiers(string text, string expected) =>
        PhiRedactor.Redact(text).Text.ShouldBe(expected);

    [Fact]
    public void Spoken_uhids_are_compacted_so_they_can_be_restored()
    {
        var result = PhiRedactor.Redact("patient id nh 2600 0123");

        result.Placeholders["[UHID_1]"].ShouldBe("NH26000123");
    }

    [Fact]
    public void Removes_the_context_patients_names_and_identifiers()
    {
        var result = PhiRedactor.Redact("Ravi Kumar Sharma (IP-2026-0042) seen with wife Priya. Mr Sharma reports fever.", Patient);

        result.Text.ShouldBe("[NAME_1] ([ID_1]) seen with wife [NAME_3]. Mr [NAME_2] reports fever.");
        result.Counts["NAME"].ShouldBe(3);
    }

    [Fact]
    public void Names_match_regardless_of_case_and_spacing_but_never_inside_other_words()
    {
        var context = new RedactionContext(["Ravi Kumar"], []);

        PhiRedactor.Redact("RAVI   kumar came with Ravindra; Ravi waited.", context).Text
            .ShouldBe("[NAME_1] came with Ravindra; [NAME_2] waited.");
    }

    [Fact]
    public void The_same_value_always_gets_the_same_placeholder()
    {
        var result = PhiRedactor.Redact("Call 9876543210 or 9876543210 again; alternate 9123456789.");

        result.Text.ShouldBe("Call [PHONE_1] or [PHONE_1] again; alternate [PHONE_2].");
        result.Counts["PHONE"].ShouldBe(2);
    }

    [Fact]
    public void Leaves_clinical_content_untouched()
    {
        const string dictation = "BP 130/80 mmHg, pulse 88/min, Hb 10.2 g/dL, T 101.2 F, RBS 180 mg/dL. Tab paracetamol 650 mg TDS x 5 days. Review on 02/10.";

        var result = PhiRedactor.Redact(dictation, Patient);

        result.Text.ShouldBe(dictation);
        result.Total.ShouldBe(0);
    }

    [Fact]
    public void Restores_placeholders_a_model_copied_back()
    {
        var result = PhiRedactor.Redact("open UHID NH26000123");

        result.Restore("Open [UHID_1] and [UNKNOWN_9]").ShouldBe("Open NH26000123 and [UNKNOWN_9]");
    }

    [Fact]
    public void Builds_name_variants_longest_first_without_honorifics_or_initials() =>
        PhiRedactor.NameVariants(["  Dr.  Anil   Mehta ", "A", "Smt. Lakshmi"])
            .ShouldBe(["Dr. Anil Mehta", "Smt. Lakshmi", "Lakshmi", "Mehta", "Anil"]);
}
