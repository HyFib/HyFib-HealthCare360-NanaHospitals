using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Organization;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Documents;

public sealed class HealthCheckupReportDocumentTests
{
    // 27 Sep 2026, 8:30 am IST.
    private static readonly DateTimeOffset Day = new(2026, 9, 27, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid DoctorId = Guid.NewGuid();
    private static readonly Guid PatientId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_summary_comes_first_with_the_package_the_doctor_s_words_the_investigations_and_the_stations_in_order()
    {
        var html = (await HealthCheckupReportDocument.BuildAsync(Checkup(), Results(), Frame(), Ct)).ToHtml();

        html.ShouldContain("<h1 class=\"doc-title\">Health check-up report</h1>");
        html.ShouldContain("Executive health check (HC-EXEC)");
        html.ShouldContain("Acme &lt;Pvt&gt; Ltd");
        html.ShouldContain("<h2>Doctor's summary</h2>");
        html.ShouldContain("<p>Mild dyslipidaemia.<br>Otherwise normal.</p>");
        html.ShouldContain("<h2>Recommendations</h2><p>Diet control; repeat lipid profile in 3 months</p>");
        html.ShouldContain("<tr><td>Complete blood count</td><td>Reported</td></tr>");
        html.ShouldContain("<tr><td>Lipid profile</td><td>In progress</td></tr>");
        html.ShouldNotContain("Ultrasound abdomen</td><td>");
        html.ShouldContain("The released results follow this summary.");
        html.IndexOf("Blood and urine samples", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("Chest X-ray</td>", StringComparison.Ordinal));
        html.ShouldContain("<td>Not done</td><td>Pregnancy not ruled out</td>");
        html.IndexOf("Doctor's summary", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("Laboratory results", StringComparison.Ordinal));
        html.ShouldNotContain("class=\"banner\"");
    }

    [Fact]
    public async Task Every_released_result_follows_laid_out_as_in_its_own_report()
    {
        var html = (await HealthCheckupReportDocument.BuildAsync(Checkup(), Results(), Frame(), Ct)).ToHtml();

        html.ShouldContain("<h2 class=\"part\">Laboratory results</h2>");
        html.ShouldContain("Report NHLB2600077 · Released 27 Sep 2026, 2:00 pm");
        html.ShouldContain("<h2>Biochemistry</h2>");
        html.ShouldContain("<span class=\"abn\">232</span>");
        html.ShouldContain("<span class=\"\">140*</span>");
        html.ShouldContain("<h2 class=\"part\">Radiology reports</h2>");
        html.ShouldContain("Report NHRD260000045");
        html.ShouldContain("<div class=\"impression\"><p>Acute left MCA infarct.</p></div>");
        html.ShouldContain("<h2 class=\"part\">Cardiology reports</h2>");
        html.ShouldContain("<tr><th style=\"width:35%\">Heart rate</th><td>118 /min</td></tr>");
        html.ShouldContain(".abg { width: auto;");
        html.ShouldContain(".addendum { border-left");
        html.ShouldContain(".report .body table.measures th");
    }

    [Fact]
    public async Task What_is_not_released_is_listed_as_pending_and_never_printed_with_a_value()
    {
        var results = Results() with
        {
            Lab = [Lab(released: true), Lab(released: false) with { ReportNumber = "NHLB2600078" }],
            Radiology = [RadiologyReportDocumentTests.Report(), Unreleased()],
        };

        var html = (await HealthCheckupReportDocument.BuildAsync(Checkup(), results, Frame(), Ct)).ToHtml();

        html.ShouldContain("<h2 class=\"part\">Results pending</h2>");
        html.ShouldContain("<tr><td>Thyroid profile</td><td>Laboratory</td><td>Pending</td></tr>");
        html.ShouldContain("<tr><td>HbA1c</td><td>Laboratory</td><td>In progress</td></tr>");
        html.ShouldContain("<tr><td>USG abdomen</td><td>Radiology</td><td>In progress</td></tr>");
        html.ShouldNotContain("8.9");
        html.ShouldNotContain("NHLB2600078");
        html.ShouldNotContain("Fatty liver");
        html.ShouldNotContain("NHRD260000050");
    }

    [Fact]
    public void Released_keeps_released_tests_and_reports_and_moves_the_rest_to_pending()
    {
        var released = HealthCheckupReportDocument.Released(Results() with
        {
            Lab = [Lab(released: true) with { Sections = [Section(Test("LIPID", "Lipid profile", LabOrderItemStatus.Released), Test("HBA1C", "HbA1c", LabOrderItemStatus.ResultsEntered))] }],
            Radiology = [Unreleased()],
        });

        released.Lab.ShouldHaveSingleItem().Sections.ShouldHaveSingleItem().Tests.ShouldHaveSingleItem().Name.ShouldBe("Lipid profile");
        released.Radiology.ShouldBeEmpty();
        released.Cardiology.ShouldHaveSingleItem();
        released.Pending.Select(p => p.Name).ShouldBe(["Thyroid profile", "HbA1c", "USG abdomen"]);
    }

    [Fact]
    public async Task Whoever_signed_the_results_signs_once_with_the_reviewing_doctor_last()
    {
        var html = (await HealthCheckupReportDocument.BuildAsync(Checkup(), Results(), Frame(), Ct)).ToHtml();
        var signatories = HealthCheckupReportDocument.Signatories(Checkup(), Results() with { Lab = [Lab(released: true), Lab(released: true)] });

        signatories.Select(s => s.Name).ShouldBe(["Dr Meera Shah", "Dr Sunita Rao", "Dr Anil Desai", "Dr. Arjun Menon"]);
        html.ShouldContain("Reg. no. 12345 (TCMC)");
        html.IndexOf("Dr Meera Shah", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("Dr. Arjun Menon", StringComparison.Ordinal));
        html.ShouldContain("— End of report —");
    }

    [Fact]
    public void A_reviewing_doctor_who_also_signed_a_report_is_listed_once()
    {
        var checkup = Checkup() with { ReviewingDoctor = new PrescriberInfo(DoctorId, "Dr Anil Desai", "DM Cardiology", null, "MMC-41122", null, null, null) };

        var signatories = HealthCheckupReportDocument.Signatories(checkup, Results());

        signatories.Select(s => s.Name).ShouldBe(["Dr Meera Shah", "Dr Sunita Rao", "Dr Anil Desai"]);
        signatories[^1].UserId.ShouldBe(DoctorId);
    }

    [Fact]
    public void Without_a_reviewing_doctor_there_is_a_space_for_one_to_sign()
    {
        var signatories = HealthCheckupReportDocument.Signatories(Checkup() with { ReviewingDoctor = null }, CheckupResults.None);

        signatories.ShouldHaveSingleItem().Name.ShouldBe("Reviewing doctor");
    }

    [Fact]
    public async Task An_unfinished_check_up_says_so_and_prints_what_it_has()
    {
        var checkup = Checkup(BookingStatus.InProgress) with { Booking = Checkup(BookingStatus.InProgress).Booking with { Summary = null, Recommendations = " ", CompletedAt = null } };

        var html = (await HealthCheckupReportDocument.BuildAsync(checkup, CheckupResults.None, Frame(), Ct)).ToHtml();

        html.ShouldContain("<div class=\"banner\">In progress — not final</div>");
        html.ShouldContain("<p class=\"muted\">Not yet written.</p>");
        html.ShouldContain("No results have been released yet.");
        html.ShouldNotContain("<h2>Recommendations</h2>");
        html.ShouldNotContain("class=\"part\"");
    }

    [Fact]
    public void The_patient_block_shows_identity_booking_package_and_dates()
    {
        var rows = HealthCheckupReportDocument.PatientRows(Checkup()).ToDictionary(r => r.Label, r => r.Value);

        rows["Patient"].ShouldBe("Meena <Iyer>");
        rows["Age / sex"].ShouldBe("46Y / F");
        rows["UHID"].ShouldBe("NH0001");
        rows["Booking no."].ShouldBe("NHHC26000042");
        rows["Package"].ShouldBe("Executive health check (HC-EXEC)");
        rows["Check-up date"].ShouldBe("27 Sep 2026");
        rows["Company"].ShouldBe("Acme <Pvt> Ltd");
        rows["Completed"].ShouldBe("27 Sep 2026, 3:00 pm");
    }

    [Fact]
    public void A_check_up_for_no_company_and_an_unknown_sex_prints_neither()
    {
        var checkup = Checkup() with
        {
            Booking = Checkup().Booking with { CorporateName = null, CompletedAt = null },
            Patient = Checkup().Patient with { Gender = Gender.Unknown, Age = null },
        };

        var rows = HealthCheckupReportDocument.PatientRows(checkup).ToDictionary(r => r.Label, r => r.Value);

        rows.ShouldNotContainKey("Company");
        rows["Age / sex"].ShouldBe("— / —");
        rows["Completed"].ShouldBe("Not completed");
    }

    [Fact]
    public async Task The_document_is_named_for_the_booking_and_every_page_says_whose_it_is()
    {
        var document = await HealthCheckupReportDocument.BuildAsync(Checkup(), Results(), Frame(), Ct);
        var html = document.ToHtml();

        document.FileName.ShouldBe("health-checkup-report-NHHC26000042");
        document.Title.ShouldBe("Health check-up report NHHC26000042");
        document.Footer.ShouldBe("Meena <Iyer> · UHID NH0001 · Health check-up report NHHC26000042");
        html.ShouldContain("Nana Hospitals");
        html.ShouldContain("NABH accreditation H-2021-0456");
        html.ShouldNotContain("<Iyer>");
        html.ShouldNotContain("<Pvt>");
        html.ShouldNotContain("Verification code");
    }

    [Fact]
    public void Notes_explain_auto_verified_values_amended_lab_reports_imaging_opinions_and_device_recordings()
    {
        var notes = HealthCheckupReportDocument.Notes(Results() with { Lab = [Lab(released: true) with { Status = LabReportStatus.Amended }] });

        notes.Count.ShouldBe(4);
        notes[0].ShouldStartWith("* Verified automatically");
        notes[1].ShouldContain("amended after release");
        notes[2].ShouldContain("professional opinion");
        notes[3].ShouldContain("Device recordings");
        HealthCheckupReportDocument.Notes(CheckupResults.None).ShouldBeEmpty();
    }

    [Fact]
    public async Task Hostile_text_in_the_check_up_is_printed_as_text()
    {
        var checkup = Checkup() with
        {
            Booking = Checkup().Booking with
            {
                Summary = "<script>alert(1)</script>",
                Itinerary = [new ItineraryItemDto(Guid.NewGuid(), 1, null, "<b>Lab</b>", "Samples", ItineraryStatus.Done, Day, "<img src=x onerror=alert(2)>")],
            },
        };
        var results = Results() with { Pending = [new PendingInvestigation("</td><td>Lipid", "Laboratory", PendingInvestigation.NotStarted)] };

        var html = (await HealthCheckupReportDocument.BuildAsync(checkup, results, Frame(), Ct)).ToHtml();

        html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.ShouldContain("&lt;b&gt;Lab&lt;/b&gt;");
        html.ShouldContain("&lt;img src=x onerror=alert(2)&gt;");
        html.ShouldContain("&lt;/td&gt;&lt;td&gt;Lipid");
        html.ShouldNotContain("<script>");
        html.ShouldNotContain("<img src=x");
        html.ShouldNotContain("<b>Lab");
    }

    [Fact]
    public void The_builder_keeps_only_the_booking_s_tests_of_a_lab_report()
    {
        var lipid = Guid.NewGuid();
        var view = Lab(released: true) with
        {
            Sections =
            [
                Section(Test("LIPID", "Lipid profile", LabOrderItemStatus.Released) with { OrderItemId = lipid }),
                new LabReportSection(LabSection.Hematology, [Test("CBC", "Complete blood count", LabOrderItemStatus.Released)]),
            ],
        };

        var mine = HealthCheckupResultsBuilder.ForBooking(view, new HashSet<Guid> { lipid });

        mine.ShouldNotBeNull().Sections.ShouldHaveSingleItem().Tests.ShouldHaveSingleItem().Name.ShouldBe("Lipid profile");
        HealthCheckupResultsBuilder.ForBooking(view, new HashSet<Guid>()).ShouldBeNull();
        HealthCheckupResultsBuilder.ForBooking(null, new HashSet<Guid> { lipid }).ShouldBeNull();
    }

    // The frame reads images only for the file ids it is given; these reports have none.
    private static ReportDocumentFrame Frame() =>
        new(new DocumentAssets(null!, Substitute.For<IFileStore>(), new MemoryCache(new MemoryCacheOptions())));

    private static CheckupResults Results() => new(
        [Lab(released: true)],
        [RadiologyReportDocumentTests.Report()],
        [CardiologyReportDocumentTests.Report()],
        [new PendingInvestigation("Thyroid profile", "Laboratory", PendingInvestigation.NotStarted)]);

    private static CheckupReport Checkup(BookingStatus status = BookingStatus.Completed)
    {
        var orderId = Guid.NewGuid();
        var booking = new BookingDto(
            Guid.NewGuid(), "NHHC26000042", PatientId, "NH0001", "Meena Iyer", Guid.NewGuid(), "HC-EXEC", "Executive health check", new DateOnly(2026, 9, 27),
            status, null, orderId, null, "Acme <Pvt> Ltd", null, Day, "Mild dyslipidaemia.\nOtherwise normal.", "Diet control; repeat lipid profile in 3 months",
            DoctorId, Day.AddHours(6), Day.AddHours(6.5), null,
            [
                new ItineraryItemDto(Guid.NewGuid(), 2, null, "Radiology", "Chest X-ray", ItineraryStatus.Skipped, null, "Pregnancy not ruled out"),
                new ItineraryItemDto(Guid.NewGuid(), 1, null, "Laboratory", "Blood and urine samples", ItineraryStatus.Done, Day.AddHours(0.5), null),
            ],
            3);
        var order = new OrderDto(
            orderId, "NHOR26000099", PatientId, null, null, null, null, null, OrderPriority.Routine, ClinicalOrderStatus.Active, OrderSource.HealthCheckup, Day, null,
            [
                OrderItem("Complete blood count", OrderItemStatus.Completed),
                OrderItem("Lipid profile", OrderItemStatus.InProgress),
                OrderItem("Ultrasound abdomen", OrderItemStatus.Cancelled),
            ]);
        return new CheckupReport(
            booking,
            new PatientSummary(PatientId, "NH0001", "Meena <Iyer>", Gender.Female, new DateOnly(1980, 1, 1), "46Y", null, BloodGroup.OPositive,
                PatientCategory.Corporate, false, false, null),
            new FacilityLetterhead(Guid.NewGuid(), "NH", "Nana Hospitals", null, "12 MG Road, Pune", "020 4000 1000", "care@nana.in", "https://nana.in",
                "REG-1", "H-2021-0456", null, null, null),
            new PrescriberInfo(DoctorId, "Dr. Arjun Menon", "MD", "TCMC", "12345", null, null, null),
            order);
    }

    private static OrderItemDto OrderItem(string name, OrderItemStatus status) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name.ToUpperInvariant(), name, ServiceCategory.Laboratory, 1, status, null, null, null, null, null);

    // A released lipid profile (one value auto-verified) and, on the unreleased copy, an HbA1c still being checked.
    private static LabReportView Lab(bool released)
    {
        var tests = released
            ? new[] { Test("LIPID", "Lipid profile", LabOrderItemStatus.Released) }
            : [Test("HBA1C", "HbA1c", LabOrderItemStatus.Verified)];
        return new LabReportView(
            Guid.NewGuid(), Guid.NewGuid(), "NHLB2600077", released ? LabReportStatus.Released : LabReportStatus.Verified, false, 0,
            new ReportLetterhead("Nana Hospitals", null, null, null, null, null, null),
            new ReportPatient(PatientId, "NH0001", "Meena <Iyer>", Gender.Female, "46Y", null),
            PatientClass.OutPatient, null, null, Day, released ? Day.AddHours(5.5) : null, released ? Day.AddHours(5.5) : null,
            [Section(tests)],
            [new ReportSignatory(Guid.Parse("0199a0f6-0000-7000-8000-000000000001"), "Dr Meera Shah", "MD Pathology", "MMC-55821", null)],
            true, [], released ? "K7Q2M9X4" : null, null, null);
    }

    private static LabReportSection Section(params LabReportTest[] tests) => new(LabSection.Biochemistry, tests);

    private static LabReportTest Test(string code, string name, LabOrderItemStatus status)
    {
        LabReportResultLine Line(string analyte, string value, ResultFlag flag, bool auto = false) =>
            new(Guid.NewGuid(), analyte, null, value, "mg/dL", flag, "< 200", LabResultStatus.Verified, auto, null, null, null, [], null, null);

        IReadOnlyList<LabReportResultLine> results = code == "HBA1C"
            ? [Line("HbA1c", "8.9", ResultFlag.High)]
            : [Line("Total cholesterol", "232", ResultFlag.High), Line("Triglycerides", "140", ResultFlag.Normal, auto: true)];
        return new LabReportTest(Guid.NewGuid(), code, name, status, SpecimenType.Serum, null, null, null, Day, Day.AddHours(1), Day.AddHours(5), null, results);
    }

    private static RadiologyReportView Unreleased() => RadiologyReportDocumentTests.Report() with
    {
        AccessionNumber = "NHRD260000050",
        StudyName = "USG abdomen",
        Findings = "Fatty liver grade I.",
        Impression = "Fatty liver.",
        Status = DiagnosticReportStatus.Verified,
        ReleasedAt = null,
    };
}
