using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Documents;

public sealed class CardiologyReportDocumentTests
{
    // 26 Sep 2026, 8:30 am IST.
    private static readonly DateTimeOffset Ordered = new(2026, 9, 26, 3, 0, 0, TimeSpan.Zero);

    private static readonly EcgFindings Ecg = new()
    {
        HeartRate = 118,
        Rhythm = "Sinus tachycardia",
        PrInterval = 140,
        QrsDuration = 90,
        QtInterval = 320,
        QtcInterval = 449,
        Axis = "Normal",
        StTChanges = "ST elevation in V1–V4 <2 mm>",
        Interpretation = null,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Ecg_measurements_are_listed_with_their_units_and_unrecorded_ones_are_left_out()
    {
        var rows = CardiologyReportDocument.MeasurementRows(Report());

        rows[0].ShouldBe(("Heart rate", "118 /min"));
        rows.ShouldContain(("QTc", "449 ms"));
        rows.ShouldContain(("ST-T changes", "ST elevation in V1–V4 <2 mm>"));
        rows.Select(r => r.Label).ShouldNotContain("Interpretation");
    }

    [Fact]
    public void Treadmill_measurements_are_formatted_as_the_staff_app_shows_them()
    {
        var tmt = new TmtFindings
        {
            Protocol = "Bruce",
            DurationSeconds = 545,
            Mets = 10.1m,
            MaxHeartRate = 152,
            TargetHeartRatePercent = 89,
            ReasonForTermination = "Fatigue",
            Result = TmtResult.Negative,
        };

        var rows = CardiologyReportDocument.MeasurementRows(Report() with { StudyType = CardiologyStudyType.Tmt, Tmt = tmt });

        rows.ShouldBe(
        [
            ("Protocol", "Bruce"),
            ("Exercise duration", "9:05 min"),
            ("Workload", "10.1 METs"),
            ("Maximum heart rate", "152 /min"),
            ("Target heart rate reached", "89 %"),
            ("Reason for stopping", "Fatigue"),
            ("Result", "Negative for inducible ischaemia"),
        ]);
    }

    [Fact]
    public void Holter_abpm_and_echo_values_use_indian_digit_grouping_and_keep_their_decimals()
    {
        var holter = CardiologyReportDocument.MeasurementRows(Report() with
        {
            StudyType = CardiologyStudyType.Holter,
            Holter = new HolterFindings { DurationHours = 24, MinHeartRate = 52, MaxHeartRate = 131, VentricularEctopics = 123456, Pauses = 0 },
        });
        var abpm = CardiologyReportDocument.MeasurementRows(Report() with
        {
            StudyType = CardiologyStudyType.Abpm,
            Abpm = new AbpmFindings { DaytimeMeanSystolic = 142, DaytimeMeanDiastolic = 88, NighttimeMeanSystolic = 130, DippingPattern = "Non-dipper" },
        });
        var echo = CardiologyReportDocument.MeasurementRows(Report() with
        {
            StudyType = CardiologyStudyType.Echo,
            Echo = new EchoFindings { Lvef = 55.5m, Valves = "Normal" },
        });

        holter.ShouldContain(("Recording", "24 hours"));
        holter.ShouldContain(("Heart rate (min / mean / max)", "52 / — / 131 /min"));
        holter.ShouldContain(("Ventricular ectopics", "1,23,456"));
        holter.ShouldContain(("Pauses", "0"));
        abpm.ShouldBe([("Daytime mean BP", "142/88 mmHg"), ("Dipping pattern", "Non-dipper")]);
        echo.ShouldBe([("LV ejection fraction", "55.5 %"), ("Valves", "Normal")]);
    }

    [Fact]
    public void Only_the_measurements_of_the_study_type_are_printed()
    {
        CardiologyReportDocument.MeasurementRows(Report() with { StudyType = CardiologyStudyType.Other }).ShouldBeEmpty();
        CardiologyReportDocument.MeasurementRows(Report() with { StudyType = CardiologyStudyType.Echo }).ShouldBeEmpty();
    }

    [Fact]
    public void The_body_prints_the_measurements_the_conclusion_and_the_critical_note_encoded()
    {
        var body = CardiologyReportDocument.Body(Report()).ToString();

        body.ShouldContain("<h2>ECG</h2>");
        body.ShouldContain("<div class=\"small\">12-lead ECG</div>");
        body.ShouldContain("<p class=\"crit\">Critical finding: communicated to the treating doctor.</p>");
        body.ShouldContain("<table class=\"measures\">");
        body.ShouldContain("<tr><th style=\"width:35%\">Heart rate</th><td>118 /min</td></tr>");
        body.ShouldContain("ST elevation in V1–V4 &lt;2 mm&gt;");
        body.ShouldContain("<h3>Conclusion</h3><div class=\"impression\"><p>Acute anterior STEMI pattern.</p></div>");
        body.ShouldContain("<h3>Recommendations</h3><p>Urgent cardiology review.</p>");
        body.ShouldNotContain("<2 mm>");
    }

    [Fact]
    public void A_report_with_nothing_measured_or_concluded_prints_only_the_study()
    {
        var body = CardiologyReportDocument.Body(
            Report() with { Ecg = null, Conclusion = " ", Recommendations = null, IsCriticalFinding = false }).ToString();

        body.ShouldContain("<h2>ECG</h2>");
        body.ShouldNotContain("<table");
        body.ShouldNotContain("Conclusion");
        body.ShouldNotContain("Recommendations");
        body.ShouldNotContain("Critical finding");
    }

    [Fact]
    public void The_patient_block_shows_identity_referral_and_times()
    {
        var rows = CardiologyReportDocument.PatientRows(Report()).ToDictionary(r => r.Label, r => r.Value);

        rows["Age / sex"].ShouldBe("61Y / M");
        rows["Accession no."].ShouldBe("NHCD260000007");
        rows["Referred by"].ShouldBe("Self");
        rows["Recorded"].ShouldBe("26 Sep 2026, 8:40 am");
        rows["Reported"].ShouldBe("26 Sep 2026, 8:50 am");
    }

    [Theory]
    [InlineData(CardiologyStudyType.Ecg, "ECG report")]
    [InlineData(CardiologyStudyType.Echo, "Echo report")]
    [InlineData(CardiologyStudyType.Tmt, "TMT report")]
    [InlineData(CardiologyStudyType.Holter, "Holter report")]
    [InlineData(CardiologyStudyType.Abpm, "ABPM report")]
    [InlineData(CardiologyStudyType.Other, "Cardiology report")]
    public void The_title_names_the_test(CardiologyStudyType type, string title) => CardiologyReportDocument.Title(type).ShouldBe(title);

    [Fact]
    public async Task The_document_carries_the_letterhead_the_qr_code_the_device_recordings_and_a_footer_on_every_page()
    {
        var document = await CardiologyReportDocument.BuildAsync(Report(), Frame(), DocumentText.English, Ct);
        var html = document.ToHtml();

        document.FileName.ShouldBe("cardiology-report-NHCD260000007");
        document.Title.ShouldBe("Cardiology report NHCD260000007");
        document.Footer.ShouldBe("Sunil <Patil> · UHID NH26000009 · Cardiology report NHCD260000007");
        html.ShouldContain("<h1 class=\"doc-title\">ECG report</h1>");
        html.ShouldContain("Nana Hospitals");
        html.ShouldContain("https://portal.nana.in/portal/verify/cardiology/Q4M8K2X7");
        html.ShouldContain("Dr Anil Desai");
        html.ShouldContain("Reg. no. MMC-41122");
        html.ShouldContain("2 device recordings are stored with this report.");
        html.ShouldContain("— End of report —");
        html.ShouldNotContain("class=\"banner\"");
        html.ShouldNotContain("<Patil>");
    }

    [Fact]
    public async Task A_report_for_a_patient_who_reads_hindi_has_hindi_headings_and_keeps_the_measurements_as_written()
    {
        var document = await CardiologyReportDocument.BuildAsync(Report(), Frame(), DocumentText.Hindi, Ct);
        var html = document.ToHtml();

        document.Language.ShouldBe("hi");
        document.Title.ShouldBe("हृदय जाँच रिपोर्ट NHCD260000007");
        document.FileName.ShouldBe("cardiology-report-NHCD260000007");
        document.Footer.ShouldBe("Sunil <Patil> · UHID NH26000009 · हृदय जाँच रिपोर्ट NHCD260000007");
        html.ShouldContain("<html lang=\"hi\">");
        html.ShouldContain("<h1 class=\"doc-title\">ECG रिपोर्ट</h1>");
        html.ShouldContain("<span class=\"label\">रोगी</span>");
        html.ShouldContain("<span class=\"label\">एक्सेशन सं.</span>");
        html.ShouldContain("<span class=\"label\">रिकॉर्ड किया गया</span>");
        html.ShouldContain("<p class=\"crit\">गंभीर निष्कर्ष: इलाज करने वाले डॉक्टर को सूचित किया गया।</p>");
        html.ShouldContain("<h3>निष्कर्ष</h3><div class=\"impression\"><p>Acute anterior STEMI pattern.</p></div>");
        html.ShouldContain("<h3>सुझाव</h3><p>Urgent cardiology review.</p>");
        html.ShouldContain("इस रिपोर्ट के साथ उपकरण की 2 रिकॉर्डिंग सुरक्षित हैं।");
        html.ShouldContain("पंजीकरण सं. MMC-41122");
        html.ShouldContain("— रिपोर्ट समाप्त —");
        html.ShouldContain("@bottom-right { content: \"पृष्ठ \" counter(page) \" / \" counter(pages);");
        html.ShouldContain("@font-face { font-family: \"HC360 Devanagari\"");

        // The study, its type and the measurements stay as clinicians write them.
        html.ShouldContain("<h2>ECG</h2>");
        html.ShouldContain("<div class=\"small\">12-lead ECG</div>");
        html.ShouldContain("<tr><th style=\"width:35%\">Heart rate</th><td>118 /min</td></tr>");
        html.ShouldNotContain("Recommendations");
    }

    [Fact]
    public void One_device_recording_is_counted_in_the_singular_and_none_gives_no_note()
    {
        CardiologyReportDocument.Notes(Report() with { Files = [new DeviceFileDto(Guid.NewGuid(), "ecg.pdf", "application/pdf", 1024)] })
            .ShouldBe(["1 device recording is stored with this report."]);
        CardiologyReportDocument.Notes(Report() with { Files = [] }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(DiagnosticReportStatus.Verified, "Signed, not yet released")]
    [InlineData(DiagnosticReportStatus.Reported, "Draft: not signed by the cardiologist")]
    [InlineData(DiagnosticReportStatus.Draft, "Draft: not signed by the cardiologist")]
    public async Task A_copy_that_is_not_released_says_so_across_the_page(DiagnosticReportStatus status, string banner)
    {
        var html = (await CardiologyReportDocument.BuildAsync(Report() with { Status = status, ReleasedAt = null }, Frame(), DocumentText.English, Ct)).ToHtml();

        html.ShouldContain($"<div class=\"banner\">{banner}</div>");
    }

    // The frame reads images only for the file ids it is given; this report has none.
    private static ReportDocumentFrame Frame() =>
        new(new DocumentAssets(null!, Substitute.For<IFileStore>(), new MemoryCache(new MemoryCacheOptions())));

    internal static CardiologyReportView Report() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        new ReportLetterhead("Nana Hospitals", "12 MG Road, Pune", "020 4000 1000", "cardiology@nana.in", "H-2021-0456", null, null),
        new ReportPatient(Guid.NewGuid(), "NH26000009", "Sunil <Patil>", Gender.Male, "61Y", null),
        "NHCD260000007",
        "ECG",
        CardiologyStudyType.Ecg,
        null,
        Ordered,
        Ordered.AddMinutes(10),
        Ecg,
        null,
        null,
        null,
        null,
        "Acute anterior STEMI pattern.",
        "Urgent cardiology review.",
        true,
        DiagnosticReportStatus.Released,
        Ordered.AddMinutes(18),
        Ordered.AddMinutes(20),
        [new ReportSignatory(Guid.NewGuid(), "Dr Anil Desai", "DM Cardiology", "MMC-41122", null)],
        [new DeviceFileDto(Guid.NewGuid(), "ecg-1.pdf", "application/pdf", 2048), new DeviceFileDto(Guid.NewGuid(), "ecg-2.pdf", "application/pdf", 2048)],
        "Q4M8K2X7",
        "/api/v1/cardiology/reports/verify/Q4M8K2X7",
        "https://portal.nana.in/portal/verify/cardiology/Q4M8K2X7");
}
