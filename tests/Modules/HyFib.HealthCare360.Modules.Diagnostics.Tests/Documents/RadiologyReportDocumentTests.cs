using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Documents;

public sealed class RadiologyReportDocumentTests
{
    // 26 Sep 2026, 8:30 am IST.
    private static readonly DateTimeOffset Ordered = new(2026, 9, 26, 3, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_report_prints_its_sections_in_reading_order_with_the_critical_note_and_addenda()
    {
        var body = RadiologyReportDocument.Body(Report()).ToString();

        body.ShouldContain("<h2>CT head (plain)</h2>");
        body.ShouldContain("<div class=\"small\">CT scan · Head</div>");
        body.IndexOf("Clinical history", StringComparison.Ordinal).ShouldBeLessThan(body.IndexOf("Findings", StringComparison.Ordinal));
        body.IndexOf("Findings", StringComparison.Ordinal).ShouldBeLessThan(body.IndexOf("Impression", StringComparison.Ordinal));
        body.ShouldContain("Axial sections &lt;5 mm&gt;.");
        body.ShouldContain("<p>Hypodensity in the left MCA territory.</p><p>No haemorrhage.</p>");
        body.ShouldContain("<h3>Impression</h3><div class=\"impression\"><p>Acute left MCA infarct.</p></div>");
        body.ShouldContain("<p class=\"crit\">Critical finding: communicated to the treating doctor.</p>");
        body.ShouldContain("<h2>Addenda</h2>");
        body.ShouldContain("27 Sep 2026, 10:30 am · Dr. Rao · MRI done · <span class=\"crit\">critical finding</span>");
        body.ShouldContain("<p>Compared with MRI: confirmed.</p>");
        body.ShouldNotContain("Comparison</h3>");
    }

    [Fact]
    public void A_report_without_addenda_or_a_critical_finding_prints_neither()
    {
        var body = RadiologyReportDocument.Body(Report() with { Addenda = [], IsCriticalFinding = false, BodyPart = null }).ToString();

        body.ShouldContain("<div class=\"small\">CT scan</div>");
        body.ShouldNotContain("Addenda");
        body.ShouldNotContain("Critical finding");
    }

    [Fact]
    public void Hostile_text_is_printed_as_text()
    {
        var report = Report() with
        {
            StudyName = "<script>alert(1)</script>",
            Findings = "<img src=x onerror=alert(2)>",
            Addenda = [new RadiologyAddendumDto(Guid.NewGuid(), "</div><b>bold</b>", "\"quoted\"", false, Ordered, "<i>Dr</i>")],
        };

        var body = RadiologyReportDocument.Body(report).ToString();

        body.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        body.ShouldContain("&lt;img src=x onerror=alert(2)&gt;");
        body.ShouldContain("&lt;/div&gt;&lt;b&gt;bold&lt;/b&gt;");
        body.ShouldContain("&lt;i&gt;Dr&lt;/i&gt; · &quot;quoted&quot;");
        body.ShouldNotContain("<script>");
        body.ShouldNotContain("<img");
        body.ShouldNotContain("<b>");
    }

    [Fact]
    public void The_patient_block_shows_identity_referral_and_times()
    {
        var rows = RadiologyReportDocument.PatientRows(Report()).ToDictionary(r => r.Label, r => r.Value);

        rows["Patient"].ShouldBe("Ravi <Kumar>");
        rows["Age / sex"].ShouldBe("56Y / M");
        rows["Accession no."].ShouldBe("NHRD260000045");
        rows["Referred by"].ShouldBe("Dr. Meena Iyer");
        rows["Ordered"].ShouldBe("26 Sep 2026, 8:30 am");
        rows["Scanned"].ShouldBe("26 Sep 2026, 9:30 am");
        rows["Reported"].ShouldBe("26 Sep 2026, 10:31 am");
    }

    [Fact]
    public void An_unsigned_report_has_no_report_time_and_a_self_referral_says_so()
    {
        var rows = RadiologyReportDocument.PatientRows(
                Report() with { ReferredBy = null, AcquiredAt = null, VerifiedAt = null, ReleasedAt = null, Status = DiagnosticReportStatus.Draft })
            .ToDictionary(r => r.Label, r => r.Value);

        rows["Referred by"].ShouldBe("Self");
        rows["Scanned"].ShouldBe("—");
        rows["Reported"].ShouldBe("Not signed");
    }

    [Fact]
    public async Task The_document_carries_the_letterhead_the_qr_code_the_signatories_and_a_footer_on_every_page()
    {
        var document = await RadiologyReportDocument.BuildAsync(Report(), Frame(), Ct);
        var html = document.ToHtml();

        document.FileName.ShouldBe("radiology-report-NHRD260000045");
        document.Title.ShouldBe("Radiology report NHRD260000045");
        document.Footer.ShouldBe("Ravi <Kumar> · UHID NH26000001 · Radiology report NHRD260000045");
        html.ShouldContain("<h1 class=\"doc-title\">Radiology report</h1>");
        html.ShouldContain("Nana Hospitals");
        html.ShouldContain("src=\"data:image/svg+xml;base64,");
        html.ShouldContain("https://portal.nana.in/portal/verify/radiology/K7Q2M9X4");
        html.ShouldContain("Dr Sunita Rao");
        html.ShouldContain("Reg. no. MMC-77310");
        html.ShouldContain("This report is a professional opinion and should be correlated clinically.");
        html.ShouldContain("— End of report —");
        html.ShouldContain(".addendum { border-left: 3px solid #999;");
        html.ShouldNotContain("class=\"banner\"");
        html.ShouldNotContain("<Kumar>");
    }

    [Theory]
    [InlineData(DiagnosticReportStatus.Verified, "Signed, not yet released")]
    [InlineData(DiagnosticReportStatus.Reported, "Draft: not signed by the radiologist")]
    [InlineData(DiagnosticReportStatus.Draft, "Draft: not signed by the radiologist")]
    public async Task A_copy_that_is_not_released_says_so_across_the_page(DiagnosticReportStatus status, string banner)
    {
        var html = (await RadiologyReportDocument.BuildAsync(Report() with { Status = status, ReleasedAt = null }, Frame(), Ct)).ToHtml();

        html.ShouldContain($"<div class=\"banner\">{banner}</div>");
    }

    // The frame reads images only for the file ids it is given; this report has none.
    private static ReportDocumentFrame Frame() =>
        new(new DocumentAssets(null!, Substitute.For<IFileStore>(), new MemoryCache(new MemoryCacheOptions())));

    internal static RadiologyReportView Report() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        new ReportLetterhead("Nana Hospitals", "12 MG Road, Pune", "020 4000 1000", "imaging@nana.in", "H-2021-0456", null, null),
        new ReportPatient(Guid.NewGuid(), "NH26000001", "Ravi <Kumar>", Gender.Male, "56Y", null),
        "NHRD260000045",
        "CT head (plain)",
        Modality.CT,
        "Head",
        "Dr. Meena Iyer",
        Ordered,
        Ordered.AddHours(1),
        "Axial sections <5 mm>.",
        "Sudden weakness of the right side.",
        null,
        "Hypodensity in the left MCA territory.\n\nNo haemorrhage.",
        "Acute left MCA infarct.",
        true,
        DiagnosticReportStatus.Released,
        Ordered.AddHours(2),
        Ordered.AddHours(2).AddMinutes(1),
        [new ReportSignatory(Guid.NewGuid(), "Dr Sunita Rao", "MD Radiodiagnosis", "MMC-77310", null)],
        [new RadiologyAddendumDto(Guid.NewGuid(), "Compared with MRI: confirmed.", "MRI done", true, Ordered.AddDays(1).AddHours(2), "Dr. Rao")],
        "K7Q2M9X4",
        "/api/v1/radiology/reports/verify/K7Q2M9X4",
        "https://portal.nana.in/portal/verify/radiology/K7Q2M9X4");
}
