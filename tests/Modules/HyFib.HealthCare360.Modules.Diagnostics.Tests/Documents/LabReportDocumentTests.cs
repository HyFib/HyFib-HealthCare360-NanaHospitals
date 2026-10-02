using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Documents;

public sealed class LabReportDocumentTests
{
    private static readonly DateTimeOffset Ordered = new(2026, 9, 25, 4, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_released_copy_prints_verified_values_and_never_unverified_ones()
    {
        var body = LabReportDocument.Body(Report(), releasedOnly: true).ToString();

        body.ShouldContain("<h2>Haematology</h2>");
        body.ShouldContain("<span class=\"crit\">5.1</span>");
        body.ShouldContain("title=\"Critically low\">LL</span>");
        body.ShouldContain("Awaited");
        body.ShouldNotContain("13.9");
        body.ShouldContain("Organism isolated: <strong>E. coli</strong> · Colony count 100000 CFU/mL");
        body.ShouldContain("Resistant (R)");
        body.ShouldContain("<strong>Interpretation:</strong> Low haemoglobin");
        body.ShouldContain("Whole blood · Method: Flow cytometry · Tested at Metro Labs");
    }

    [Fact]
    public void The_laboratory_copy_shows_work_in_progress_as_not_verified()
    {
        var body = LabReportDocument.Body(Report(), releasedOnly: false).ToString();

        body.ShouldContain("13.9</span> <span class=\"small\">(not verified)</span>");
    }

    [Fact]
    public void The_patient_block_shows_the_revision_the_referrer_and_the_first_collection()
    {
        var rows = LabReportDocument.PatientRows(Report() with { Revision = 2, ReferredBy = null }).ToDictionary(r => r.Label, r => r.Value);

        rows["Report no."].ShouldBe("NHLAB2600012 (revision 2)");
        rows["Referred by"].ShouldBe("Self");
        rows["Age / sex"].ShouldBe("34 y / F");
        rows["Collected"].ShouldBe("25 Sep 2026, 11:00 am");
        rows["Released"].ShouldBe("25 Sep 2026, 5:30 pm");
    }

    [Fact]
    public void Notes_explain_partial_auto_verified_and_amended_reports()
    {
        var notes = LabReportDocument.Notes(Report() with { IsPartial = true, HasAutoValidatedResults = true, Status = LabReportStatus.Amended });

        notes.Count.ShouldBe(3);
        notes[0].ShouldStartWith("Partial report");
        notes[1].ShouldStartWith("* Verified automatically");
        notes[2].ShouldContain("amended after release");
    }

    [Fact]
    public async Task The_document_carries_the_letterhead_the_qr_code_and_a_footer_on_every_page()
    {
        var document = await LabReportDocument.BuildAsync(Report(), releasedOnly: true, Frame(), Ct);
        var html = document.ToHtml();

        document.FileName.ShouldBe("lab-report-NHLAB2600012");
        document.Title.ShouldBe("Lab report NHLAB2600012");
        document.Footer.ShouldBe("Asha <Rao> · UHID NH-000123 · Lab report NHLAB2600012");
        html.ShouldContain("Nana Hospitals");
        html.ShouldContain("NABH accreditation H-2021-0456");
        html.ShouldContain("src=\"data:image/svg+xml;base64,");
        html.ShouldContain("https://portal.nana.in/portal/verify/lab/K7Q2M9X4");
        html.ShouldContain("Dr Meera Shah");
        html.ShouldContain("Reg. no. MMC-55821");
        html.ShouldContain("— End of report —");
        html.ShouldNotContain("Preliminary copy");
        html.ShouldNotContain("<Rao>");
    }

    [Fact]
    public async Task Without_a_portal_address_the_code_is_printed_without_a_qr_code()
    {
        var html = (await LabReportDocument.BuildAsync(Report() with { VerificationUrl = null }, releasedOnly: true, Frame(), Ct)).ToHtml();

        html.ShouldContain("Verification code K7Q2M9X4");
        html.ShouldNotContain("data:image/svg+xml");
    }

    [Fact]
    public async Task An_unreleased_copy_says_so_across_the_page()
    {
        var html = (await LabReportDocument.BuildAsync(Report() with { Status = LabReportStatus.Verified }, releasedOnly: false, Frame(), Ct)).ToHtml();

        html.ShouldContain("Preliminary copy for the laboratory: not released");
    }

    // The frame reads images only for the file ids it is given; this report has none.
    private static ReportDocumentFrame Frame() =>
        new(new DocumentAssets(null!, Substitute.For<IFileStore>(), new MemoryCache(new MemoryCacheOptions())));

    private static LabReportView Report()
    {
        LabReportResultLine Line(string name, string? value, LabResultStatus status, ResultFlag? flag = null, string? organism = null) =>
            new(Guid.NewGuid(), name, null, value, "g/dL", flag, "12.0 – 15.0", status, false, null, organism, organism is null ? null : "100000 CFU/mL",
                organism is null ? [] : [new SusceptibilityDto("Ciprofloxacin", SusceptibilityInterpretation.Resistant, "4")], null, null);

        var cbc = new LabReportTest(
            Guid.NewGuid(), "CBC", "Complete blood count", LabOrderItemStatus.Released, SpecimenType.WholeBlood, "Flow cytometry", "Low haemoglobin",
            "Metro Labs", Ordered.AddHours(1.5), Ordered.AddHours(2), Ordered.AddHours(6), Ordered.AddHours(8),
            [
                Line("Haemoglobin", "5.1", LabResultStatus.Verified, ResultFlag.CriticalLow),
                Line("WBC", "13.9", LabResultStatus.Entered, ResultFlag.High),
                Line("Urine culture", "Growth", LabResultStatus.Verified, organism: "E. coli"),
            ]);

        return new LabReportView(
            Guid.NewGuid(), Guid.NewGuid(), "NHLAB2600012", LabReportStatus.Released, false, 1,
            new ReportLetterhead("Nana Hospitals", "12 MG Road, Pune", "020 4000 1000", "lab@nana.in", "H-2021-0456", null, null),
            new ReportPatient(Guid.NewGuid(), "NH-000123", "Asha <Rao>", Gender.Female, "34 y", null),
            PatientClass.OutPatient, "Dr Kumar", null, Ordered, Ordered.AddHours(8), Ordered.AddHours(8),
            [new LabReportSection(LabSection.Hematology, [cbc])],
            [new ReportSignatory(Guid.NewGuid(), "Dr Meera Shah", "MD Pathology", "MMC-55821", null)],
            false, [], "K7Q2M9X4", "/api/v1/lab/reports/verify/K7Q2M9X4", "https://portal.nana.in/portal/verify/lab/K7Q2M9X4");
    }
}
