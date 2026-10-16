using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Clinical.Tests.Documents;

/// <summary>The A4 discharge summary the ward prints, staff download and the patient downloads from the portal.</summary>
public sealed class DischargeSummaryDocumentTests
{
    private static readonly DateTimeOffset Admitted = new(2026, 9, 20, 5, 0, 0, TimeSpan.Zero);

    // 8 pm UTC on the 24th is 1:30 am on the 25th in India: the file name and footer use the Indian date.
    private static readonly DateTimeOffset Discharged = new(2026, 9, 24, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedAt = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_summary_is_an_a4_page_named_after_the_uhid_and_the_discharge_date_in_india()
    {
        var document = DischargeSummaryDocument.Build(Print(), null, null);

        document.PageSize.ShouldBe(DocumentPageSize.A4);
        document.FileName.ShouldBe("discharge-summary-NH-000123-2026-09-25");
        document.Title.ShouldBe("Discharge summary NH-000123");
        document.Footer.ShouldBe("Asha Rao · UHID NH-000123 · Discharge summary · Discharged 25 Sep 2026");
        document.ToHtml().ShouldContain("size: A4");
    }

    [Fact]
    public void Without_a_discharge_date_the_file_is_named_after_the_signing_date()
    {
        var print = Print();
        print = print with { Summary = print.Summary with { DischargedAt = null } };

        DischargeSummaryDocument.FileName(print).ShouldBe("discharge-summary-NH-000123-2026-09-25");
        DischargeSummaryDocument.Footer(print).ShouldBe("Asha Rao · UHID NH-000123 · Discharge summary");
        DischargeSummaryDocument.FileName(print with { Summary = print.Summary with { SignedAt = null, AdmittedAt = null } })
            .ShouldBe("discharge-summary-NH-000123");
    }

    [Fact]
    public void The_sections_follow_the_staff_print_in_order()
    {
        var body = DischargeSummaryDocument.Body(Print(), null, null).ToString();

        string[] headings =
        [
            "<h2 class=\"summary-title\">Discharge summary</h2>", "<h3>Diagnosis</h3>", "<h3>Presenting complaints</h3>", "<h3>Course in hospital</h3>",
            "<h3>Procedures</h3>", "<h3>Investigations</h3>", "<h3>Condition at discharge</h3>", "<h3>Medicines on discharge</h3>", "<h3>Advice</h3>",
            "<h3>Follow-up</h3>",
        ];
        var positions = headings.Select(h => body.IndexOf(h, StringComparison.Ordinal)).ToList();
        positions.ShouldAllBe(p => p >= 0);
        positions.ShouldBe(positions.Order().ToList());
    }

    [Fact]
    public void The_patient_strip_the_diagnosis_codes_and_the_follow_up_are_printed()
    {
        var body = DischargeSummaryDocument.Body(Print(), null, null).ToString();

        body.ShouldContain("<div class=\"small\">Signed 25 Sep 2026, 1:30 pm</div>");
        body.ShouldContain("<div class=\"label\">Age / sex</div><div>58 y / F</div>");
        body.ShouldContain("<div class=\"label\">Admitted / discharged</div><div>20 Sep 2026 → 25 Sep 2026</div>");
        body.ShouldContain("<div class=\"pre\">Acute subendocardial myocardial infarction<br>Type 2 diabetes mellitus<br>ICD-10: I21.4, E11.9</div>");
        body.ShouldContain("<div class=\"pre\">Admitted to CCU.<br>PTCA to LAD on day 2.</div>");
        body.ShouldContain("<h3>Follow-up</h3><div>Review on 03 Oct 2026. Cardiology OPD with reports</div>");
        body.ShouldContain("<div class=\"small\">In an emergency, come to the hospital casualty or call the hospital.</div>");
        body.ShouldContain("<div class=\"line\">Dr Kavita Menon</div>");
        body.ShouldContain("<div class=\"small\">Reg. No. KMC-11223 (Karnataka Medical Council)</div>");
        body.ShouldNotContain("class=\"draft\"");
    }

    [Fact]
    public void Sections_without_text_are_left_out()
    {
        var print = Print();
        var body = DischargeSummaryDocument.Body(print with { Summary = print.Summary with { Procedures = null, Advice = "  " } }, null, null).ToString();

        body.ShouldNotContain("<h3>Procedures</h3>");
        body.ShouldNotContain("<h3>Advice</h3>");
    }

    [Fact]
    public void An_amended_summary_says_when_and_why()
    {
        var print = Print(DischargeSummaryStatus.Amended);
        print = print with
        {
            Summary = print.Summary with { AmendedAt = new DateTimeOffset(2026, 9, 26, 4, 30, 0, TimeSpan.Zero), AmendmentReason = "Corrected the aspirin dose" },
        };

        DischargeSummaryDocument.Body(print, null, null).ToString()
            .ShouldContain("<div class=\"small\">Signed 25 Sep 2026, 1:30 pm · amended 26 Sep 2026, 10:00 am (Corrected the aspirin dose)</div>");
    }

    [Fact]
    public async Task A_draft_says_so_at_the_top_and_on_every_page_and_carries_no_signature()
    {
        var signature = Guid.NewGuid();
        var print = Print(DischargeSummaryStatus.Draft);
        print = print with { Consultant = print.Consultant! with { SignatureFileId = signature } };

        var document = await DischargeSummaryDocument.BuildAsync(print, PrescriptionDocumentTests.Assets((signature, "image/png", [1, 2, 3])), DocumentText.English, Ct);
        var html = document.ToHtml();

        html.ShouldContain("<div class=\"draft\">DRAFT — not signed</div>");
        html.ShouldNotContain("Signed ");
        html.ShouldNotContain("class=\"sign-img\"");
        document.Footer.ShouldBe("Asha Rao · UHID NH-000123 · Discharge summary · Discharged 25 Sep 2026 · DRAFT — not signed");
    }

    [Fact]
    public async Task A_signed_summary_carries_the_facility_logo_and_the_consultants_signature()
    {
        var logo = Guid.NewGuid();
        var signature = Guid.NewGuid();
        var print = Print();
        print = print with { Letterhead = print.Letterhead! with { LogoFileId = logo }, Consultant = print.Consultant! with { SignatureFileId = signature } };

        var html = (await DischargeSummaryDocument.BuildAsync(
            print, PrescriptionDocumentTests.Assets((logo, "image/png", [1, 2, 3]), (signature, "image/png", [7, 8, 9])), DocumentText.English, Ct)).ToHtml();

        html.ShouldContain($"<img class=\"logo\" src=\"data:image/png;base64,{Convert.ToBase64String(new byte[] { 1, 2, 3 })}\" alt=\"\">");
        html.ShouldContain(
            $"<img class=\"sign-img\" src=\"data:image/png;base64,{Convert.ToBase64String(new byte[] { 7, 8, 9 })}\" alt=\"Signature of Dr Kavita Menon\">");
    }

    [Fact]
    public void Without_a_consultant_the_line_names_the_treating_doctor_or_says_consultant()
    {
        var print = Print() with { Consultant = null };

        DischargeSummaryDocument.Body(print, null, null).ToString().ShouldContain("<div class=\"line\">Dr Arjun Rao</div>");
        DischargeSummaryDocument.Body(print with { Summary = print.Summary with { PractitionerName = null } }, null, null).ToString()
            .ShouldContain("<div class=\"line\">Consultant</div>");
    }

    [Fact]
    public void Text_from_the_record_is_encoded_and_its_line_breaks_are_kept()
    {
        var print = Print();
        print = print with
        {
            Summary = print.Summary with { CourseInHospital = "Day 1: <b>stable</b>\nDay 2: </div><script>alert(1)</script>" },
            Patient = print.Patient with { FullName = "Asha <Rao>" },
        };

        var document = DischargeSummaryDocument.Build(print, null, null);
        var html = document.ToHtml();

        html.ShouldContain("<div class=\"pre\">Day 1: &lt;b&gt;stable&lt;/b&gt;<br>Day 2: &lt;/div&gt;&lt;script&gt;");
        html.ShouldNotContain("<script");
        html.ShouldNotContain("<b>");
        html.ShouldNotContain("<Rao>");
        document.Footer!.ShouldStartWith("Asha <Rao> · UHID NH-000123");
    }

    [Fact]
    public void For_a_patient_who_reads_hindi_the_headings_are_hindi_and_the_doctors_text_is_printed_as_written()
    {
        var document = DischargeSummaryDocument.Build(Print(), null, null, text: DocumentText.Hindi);
        var html = document.ToHtml();

        document.Language.ShouldBe("hi");
        document.Title.ShouldBe("डिस्चार्ज सारांश NH-000123");
        document.Footer.ShouldBe("Asha Rao · UHID NH-000123 · डिस्चार्ज सारांश · छुट्टी 25 Sep 2026");
        html.ShouldContain("<html lang=\"hi\">");
        html.ShouldContain("<h2 class=\"summary-title\">डिस्चार्ज सारांश</h2>");
        html.ShouldContain("<div class=\"small\">हस्ताक्षरित 25 Sep 2026, 1:30 pm</div>");
        html.ShouldContain("<div class=\"label\">रोगी</div><div>Asha Rao</div>");
        html.ShouldContain("<div class=\"label\">भर्ती / छुट्टी</div><div>20 Sep 2026 → 25 Sep 2026</div>");
        html.ShouldContain("<h3>निदान</h3><div class=\"pre\">Acute subendocardial myocardial infarction<br>Type 2 diabetes mellitus<br>ICD-10: I21.4, E11.9</div>");
        html.ShouldContain("<h3>छुट्टी पर दी गई दवाएँ</h3><div class=\"pre\">Aspirin 75 mg once a day</div>");
        html.ShouldContain("<h3>दोबारा दिखाना (फ़ॉलो-अप)</h3><div>03 Oct 2026 को दोबारा दिखाएँ। Cardiology OPD with reports</div>");
        html.ShouldContain("आपात स्थिति में अस्पताल के आपातकालीन विभाग (कैज़ुअल्टी) में आएँ या अस्पताल को फ़ोन करें।");
        html.ShouldContain("@font-face { font-family: \"HC360 Devanagari\"");
        html.ShouldNotContain("Presenting complaints");
    }

    private static DischargeSummaryPrint Print(DischargeSummaryStatus status = DischargeSummaryStatus.Signed) => new(
        new DischargeSummaryDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), "Dr Arjun Rao", Admitted, Discharged,
            "Acute subendocardial myocardial infarction\nType 2 diabetes mellitus", ["I21.4", "E11.9"], "Chest pain for 2 hours",
            "Admitted to CCU.\nPTCA to LAD on day 2.", "PTCA with drug-eluting stent to LAD", "Troponin raised; ECHO EF 50%", "Stable, ambulant",
            "Aspirin 75 mg once a day", "Low-salt diabetic diet", new DateOnly(2026, 10, 3), "Cardiology OPD with reports", status,
            status == DischargeSummaryStatus.Draft ? null : SignedAt, null, null, null, 0, 1),
        new PatientSummary(Guid.NewGuid(), "NH-000123", "Asha Rao", Gender.Female, null, "58 y", null, BloodGroup.Unknown, PatientCategory.General, false, false,
            null),
        new FacilityLetterhead(Guid.NewGuid(), "NH", "Nana Hospitals", null, "12 MG Road, Pune", "020 4000 1000", null, null, null, "H-2021-0456", null, null,
            null),
        new PrescriberInfo(Guid.NewGuid(), "Dr Kavita Menon", "MD, DM (Cardiology)", "Karnataka Medical Council", "KMC-11223", "Cardiology", "Cardiology",
            null));
}
