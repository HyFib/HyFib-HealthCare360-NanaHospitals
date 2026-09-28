using System.Text;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class DiagnosticReportMapperTests
{
    private static readonly Guid ReportId = Guid.Parse("01923a4b-0000-7000-8000-000000001001");
    private static readonly Guid ResultA = Guid.Parse("01923a4b-0000-7000-8000-000000001002");
    private static readonly Guid ResultB = Guid.Parse("01923a4b-0000-7000-8000-000000001003");

    private static LabReportRow LabReport() => new()
    {
        Id = ReportId,
        PatientId = TestData.PatientId,
        ReportNumber = "NHLB260000123",
        Status = LabReportStatus.Released,
        OrderedAt = TestData.Now.AddHours(-6),
        CollectedAt = TestData.Now.AddHours(-5),
        ReleasedAt = TestData.Now,
        ResultIds = [ResultA, ResultB],
        TestNames = ["Complete blood count", "Complete blood count", "HbA1c"],
        CreatedAt = TestData.Now.AddHours(-6),
    };

    private static RadiologyReportRow ChestXray() => new()
    {
        Id = ReportId,
        PatientId = TestData.PatientId,
        AccessionNumber = "NHRD260000042",
        ServiceCode = "RAD-XR-CHEST",
        StudyName = "X-ray chest PA view",
        Modality = Modality.XR,
        OrderedAt = TestData.Now.AddHours(-3),
        AcquiredAt = TestData.Now.AddHours(-2),
        ReleasedAt = TestData.Now,
        Technique = "PA view",
        Findings = "Lung fields are clear.",
        Impression = "Normal chest radiograph.",
        CreatedAt = TestData.Now.AddHours(-3),
    };

    private static CardiologyReportRow Ecg() => new()
    {
        Id = ReportId,
        PatientId = TestData.PatientId,
        AccessionNumber = "NHCD260000009",
        ServiceCode = "CARD-ECG",
        StudyName = "Electrocardiogram (12-lead)",
        StudyType = CardiologyStudyType.Ecg,
        OrderedAt = TestData.Now.AddHours(-1),
        AcquiredAt = TestData.Now.AddMinutes(-50),
        ReleasedAt = TestData.Now,
        Ecg = new EcgFindings { HeartRate = 72, Rhythm = "Sinus rhythm", QtcInterval = 410, Interpretation = "Normal ECG" },
        Conclusion = "Normal ECG",
        CreatedAt = TestData.Now.AddHours(-1),
    };

    private static string Text(R4.Attachment attachment) => Encoding.UTF8.GetString(attachment.Data);

    [Fact]
    public void A_laboratory_report_lists_its_released_results()
    {
        var report = DiagnosticReportMapper.FromLab(LabReport());

        report.Id.ShouldBe($"lab-{ReportId:D}");
        report.Status.ShouldBe(R4.DiagnosticReport.DiagnosticReportStatus.Final);
        report.Category.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("LAB");
        report.Code.Coding.ShouldHaveSingleItem().Code.ShouldBe("11502-2");
        report.Code.Text.ShouldBe("Complete blood count, HbA1c");
        report.Result.Select(r => r.Reference).ShouldBe([$"Observation/lab-{ResultA:D}", $"Observation/lab-{ResultB:D}"]);
        report.Identifier.ShouldHaveSingleItem().Value.ShouldBe("NHLB260000123");
        ((R4.FhirDateTime)report.Effective).Value.ShouldBe("2026-09-25T05:30:00+05:30");
        report.Issued.ShouldBe(TestData.Now);
    }

    [Fact]
    public void A_report_with_pending_tests_is_partial_and_a_corrected_one_amended()
    {
        DiagnosticReportMapper.FromLab(LabReport() with { IsPartial = true }).Status.ShouldBe(R4.DiagnosticReport.DiagnosticReportStatus.Partial);
        DiagnosticReportMapper.FromLab(LabReport() with { Status = LabReportStatus.Amended }).Status.ShouldBe(R4.DiagnosticReport.DiagnosticReportStatus.Amended);
    }

    [Fact]
    public void A_radiology_report_carries_the_impression_and_the_full_text()
    {
        var report = DiagnosticReportMapper.FromRadiology(ChestXray());

        report.Id.ShouldBe($"rad-{ReportId:D}");
        report.Status.ShouldBe(R4.DiagnosticReport.DiagnosticReportStatus.Final);
        report.Category[0].Coding[0].Code.ShouldBe("RAD");
        report.Code.Coding.Select(c => c.Code).ShouldBe(["18748-4", "RAD-XR-CHEST"]);
        report.Code.Text.ShouldBe("X-ray chest PA view");
        report.Conclusion.ShouldBe("Normal chest radiograph.");
        var form = report.PresentedForm.ShouldHaveSingleItem();
        form.ContentType.ShouldBe("text/plain; charset=utf-8");
        Text(form).ShouldContain("Findings: Lung fields are clear.");
        Text(form).ShouldContain("Impression: Normal chest radiograph.");
        form.SizeUnsignedInt.ShouldBe(form.Data.Length);
    }

    [Fact]
    public void Addenda_make_a_radiology_report_appended_and_appear_in_the_text()
    {
        var report = DiagnosticReportMapper.FromRadiology(ChestXray() with
        {
            Addenda = [new RadiologyAddendumRow("Small left pleural effusion noted on review.", "Second read", TestData.Now.AddHours(1))],
        });

        report.Status.ShouldBe(R4.DiagnosticReport.DiagnosticReportStatus.Appended);
        Text(report.PresentedForm[0]).ShouldContain("Small left pleural effusion noted on review.");
        Text(report.PresentedForm[0]).ShouldContain("reason: Second read");
    }

    [Fact]
    public void An_ecg_report_is_electrocardiac_with_its_measurements()
    {
        var report = DiagnosticReportMapper.FromCardiology(Ecg());

        report.Id.ShouldBe($"card-{ReportId:D}");
        report.Category[0].Coding[0].Code.ShouldBe("EC");
        report.Code.Coding.Select(c => c.Code).ShouldBe(["11524-6", "CARD-ECG"]);
        report.Conclusion.ShouldBe("Normal ECG");
        var text = Text(report.PresentedForm[0]);
        text.ShouldContain("Heart rate: 72 bpm");
        text.ShouldContain("QTc interval: 410 ms");
        text.ShouldNotContain("LVEF");
    }

    [Theory]
    [InlineData(CardiologyStudyType.Ecg, "EC")]
    [InlineData(CardiologyStudyType.Holter, "EC")]
    [InlineData(CardiologyStudyType.Tmt, "EC")]
    [InlineData(CardiologyStudyType.Echo, "CUS")]
    [InlineData(CardiologyStudyType.Abpm, "OTH")]
    public void Sorts_cardiology_studies_into_diagnostic_sections(CardiologyStudyType type, string section) =>
        DiagnosticReportMapper.CardiologySection(type).Coding[0].Code.ShouldBe(section);

    [Fact]
    public void An_echo_report_shows_the_ejection_fraction()
    {
        var text = DiagnosticReportMapper.CardiologyText(Ecg() with { StudyType = CardiologyStudyType.Echo, Echo = new EchoFindings { Lvef = 55.00m, Valves = "Normal" } });

        text.ShouldContain("LVEF: 55 %");
        text.ShouldContain("Valves: Normal");
        text.ShouldNotContain("QTc");
    }
}

public sealed class DocumentReferenceMapperTests
{
    private static readonly Guid DocumentId = Guid.Parse("01923a4b-0000-7000-8000-000000002001");
    private static readonly Guid AdmissionId = Guid.Parse("01923a4b-0000-7000-8000-000000002002");
    private static readonly Guid VisitId = Guid.Parse("01923a4b-0000-7000-8000-000000002003");

    private static DischargeSummaryRow Summary() => new()
    {
        Id = DocumentId,
        PatientId = TestData.PatientId,
        AdmissionId = AdmissionId,
        PractitionerId = TestData.DoctorId,
        AdmittedAt = TestData.Now.AddDays(-4),
        DischargedAt = TestData.Now,
        Diagnoses = "Community-acquired pneumonia",
        DiagnosisCodes = ["J18.9"],
        CourseInHospital = "Treated with IV antibiotics; improved.",
        ConditionAtDischarge = "Stable",
        Medications = "Tab. Amoxicillin-clavulanate 625 mg BD x 5 days",
        FollowUpDate = new DateOnly(2026, 10, 2),
        Status = Clinical.Domain.DischargeSummaryStatus.Signed,
        SignedAt = TestData.Now,
        CreatedAt = TestData.Now.AddHours(-3),
    };

    [Fact]
    public void A_discharge_summary_is_shared_as_loinc_18842_5_with_its_text()
    {
        var directory = new ReferenceDirectory
        {
            Practitioners = TestData.Directory.Practitioners,
            AdmissionVisits = new Dictionary<Guid, Guid> { [AdmissionId] = VisitId },
        };

        var document = DocumentReferenceMapper.FromDischargeSummary(Summary(), directory);

        document.Id.ShouldBe($"ds-{DocumentId:D}");
        document.Status.ShouldBe(R4.DocumentReferenceStatus.Current);
        document.DocStatus.ShouldBe(R4.CompositionStatus.Final);
        document.Type.Coding.ShouldHaveSingleItem().Code.ShouldBe("18842-5");
        document.Date.ShouldBe(TestData.Now);
        document.Author.ShouldHaveSingleItem().Display.ShouldBe("Dr. Asha Rao");
        document.Context.Encounter.ShouldHaveSingleItem().Reference.ShouldBe($"Encounter/visit-{VisitId:D}");
        document.Context.Period.Start.ShouldBe("2026-09-21T10:30:00+05:30");
        var text = Encoding.UTF8.GetString(document.Content.ShouldHaveSingleItem().Attachment.Data);
        text.ShouldContain("Diagnoses: Community-acquired pneumonia");
        text.ShouldContain("ICD-10 codes: J18.9");
        text.ShouldContain("Follow-up date: 2026-10-02");
        text.ShouldNotContain("Procedures:");
    }

    [Fact]
    public void An_amended_summary_is_amended() =>
        DocumentReferenceMapper.FromDischargeSummary(Summary() with { Status = Clinical.Domain.DischargeSummaryStatus.Amended, AmendedAt = TestData.Now.AddDays(1) }, ReferenceDirectory.Empty)
            .DocStatus.ShouldBe(R4.CompositionStatus.Amended);

    [Fact]
    public void A_patient_document_shares_its_metadata()
    {
        var document = DocumentReferenceMapper.FromPatientDocument(new PatientDocumentRow
        {
            Id = DocumentId,
            PatientId = TestData.PatientId,
            Type = Patients.Domain.PatientDocumentType.ReferralLetter,
            Title = "Referral from Dr. Joshi",
            DocumentDate = new DateOnly(2026, 9, 20),
            Source = "Joshi Clinic",
            ContentType = "application/pdf",
            SizeBytes = 48213,
            CreatedAt = TestData.Now,
        });

        document.Id.ShouldBe($"doc-{DocumentId:D}");
        document.Type.Coding.ShouldHaveSingleItem().Code.ShouldBe("57133-1");
        document.Description.ShouldBe("Referral from Dr. Joshi");
        document.Author.ShouldHaveSingleItem().Display.ShouldBe("Joshi Clinic");
        var attachment = document.Content.ShouldHaveSingleItem().Attachment;
        attachment.ContentType.ShouldBe("application/pdf");
        attachment.SizeUnsignedInt.ShouldBe(48213);
        attachment.Creation.ShouldBe("2026-09-20");
        attachment.Data.ShouldBeNull();
        attachment.Url.ShouldBeNull();
    }

    [Theory]
    [InlineData(Patients.Domain.PatientDocumentType.DischargeSummary, "18842-5")]
    [InlineData(Patients.Domain.PatientDocumentType.ReferralLetter, "57133-1")]
    [InlineData(Patients.Domain.PatientDocumentType.Consent, "59284-0")]
    [InlineData(Patients.Domain.PatientDocumentType.Prescription, "57833-6")]
    [InlineData(Patients.Domain.PatientDocumentType.Imaging, "18748-4")]
    [InlineData(Patients.Domain.PatientDocumentType.Photo, "72170-4")]
    public void Types_clinical_documents_with_loinc(Patients.Domain.PatientDocumentType type, string loinc)
    {
        var coding = DocumentReferenceMapper.Type(type).Coding.ShouldHaveSingleItem();

        coding.System.ShouldBe("http://loinc.org");
        coding.Code.ShouldBe(loinc);
    }

    [Theory]
    [InlineData(Patients.Domain.PatientDocumentType.IdProof, false)]
    [InlineData(Patients.Domain.PatientDocumentType.InsuranceCard, false)]
    [InlineData(Patients.Domain.PatientDocumentType.ExternalReport, true)]
    [InlineData(Patients.Domain.PatientDocumentType.DischargeSummary, true)]
    [InlineData(Patients.Domain.PatientDocumentType.Other, true)]
    public void Never_shares_identity_or_insurance_documents(Patients.Domain.PatientDocumentType type, bool shared) =>
        DocumentReferenceMapper.IsShared(type).ShouldBe(shared);
}
