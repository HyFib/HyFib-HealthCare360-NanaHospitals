using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Notifications;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// An ABDM document is the same whenever its record is exported, byte for byte, or a new version (SRS EHR-08): it holds
/// what the record held when it was completed, its printed PDF is the same bytes when it prints the same thing, and
/// nothing outside it (a doctor it leaves out, the order the database returned rows in) moves its version.
/// </summary>
public sealed class AbdmDocumentVersionTests
{
    private static readonly DateTimeOffset Later = TestData.Now.AddDays(30);

    [Fact]
    public void A_consultation_holds_what_its_record_held_when_it_was_signed()
    {
        // The note was signed with a prescription and its diagnoses. A month later: an allergy recorded, a diagnosis and
        // vital signs added to the encounter, another prescription signed, the first one cancelled and the fever resolved.
        var data = AbdmTestData.ConsultationRecord();
        var signed = data with { Lines = [.. data.Lines.Select(l => l with { PrescriptionUpdatedAt = l.SignedAt })] };
        var since = signed with
        {
            Allergies = [.. signed.Allergies, Allergy("Sulfonamides", Later)],
            Diagnoses =
            [
                signed.Diagnoses[0] with { Status = DiagnosisStatus.Resolved, ResolvedOn = DateOnly.FromDateTime(Later.Date), UpdatedAt = Later },
                signed.Diagnoses[1],
                signed.Diagnoses[1] with { Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ae001"), Title = "Dengue fever", CreatedAt = Later, RecordedAt = Later },
            ],
            Vitals = [.. signed.Vitals, signed.Vitals[0] with { Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ae002"), RecordedAt = Later, CreatedAt = Later }],
            Lines =
            [
                .. signed.Lines.Select(l => l with
                {
                    Status = PrescriptionStatus.Cancelled, CancelledAt = Later, CancelReason = "Changed to another brand", PrescriptionUpdatedAt = Later,
                }),
                signed.Lines[0] with
                {
                    Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ae003"), PrescriptionId = Guid.Parse("01923a4b-0000-7000-8000-0000000ae004"),
                    PrescribedAt = Later, SignedAt = Later, CreatedAt = Later,
                },
            ],
        };

        Json(AbdmRecords.Consultation(since)).ShouldBe(Json(AbdmRecords.Consultation(signed)));
    }

    [Fact]
    public void An_amended_note_holds_what_was_recorded_by_the_time_it_was_amended()
    {
        var data = AbdmTestData.ConsultationRecord();
        var recorded = data.Note.SignedAt!.Value.AddHours(1);
        var amended = data with
        {
            Note = data.Note with { Status = EncounterStatus.Amended, AmendedAt = recorded.AddHours(1), UpdatedAt = recorded.AddHours(1), AmendedByPractitionerId = TestData.DoctorId },
            Allergies = [.. data.Allergies, Allergy("Sulfonamides", recorded), Allergy("Latex", Later)],
        };

        AbdmDocumentBuilder.Build(AbdmRecords.Consultation(amended)).Entry.Select(e => e.Resource).OfType<R4.AllergyIntolerance>().Select(a => a.Code.Text)
            .ShouldBe(["Penicillin", "Sulfonamides"]);
    }

    [Fact]
    public void A_discharge_summary_holds_what_the_stay_had_when_it_was_signed()
    {
        // After the summary was signed: a lab report of the stay released late, another medicine and an allergy recorded.
        var data = AbdmTestData.DischargeSummaryRecord(pdf: null);
        var report = data.LabReports[0];
        var since = data with
        {
            LabReports = [.. data.LabReports, report with { Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ae010"), ReportNumber = "NHLB2600199", LabOrderId = Guid.Parse("01923a4b-0000-7000-8000-0000000ae011"), ReleasedAt = Later, CreatedAt = Later, UpdatedAt = Later }],
            Lines = [.. data.Lines, data.Lines[0] with { Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ae012"), SignedAt = Later, PrescribedAt = Later, CreatedAt = Later }],
            Allergies = [.. data.Allergies, Allergy("Iodine contrast", Later)],
        };

        Json(AbdmRecords.DischargeSummary(since)).ShouldBe(Json(AbdmRecords.DischargeSummary(data)));
    }

    [Fact]
    public void A_pdf_made_again_is_the_same_document_and_one_that_prints_otherwise_is_a_new_version()
    {
        // Two PDFs of the same page differ in the time they were made, which becomes the document's date.
        var data = AbdmTestData.Prescription(AbdmTestData.Pdf);
        var first = Printed(data, "D:20261021005544+00'00'", "Age: 45");
        var again = Printed(data, "D:20261121093012+00'00'", "Age: 45");

        Json(AbdmRecords.Prescription(again)).ShouldBe(Json(AbdmRecords.Prescription(first)));
        var binary = AbdmDocumentBuilder.Build(AbdmRecords.Prescription(again)).Entry.Select(e => e.Resource).OfType<R4.Binary>().Single();
        var signed = data.SignedAt!.Value.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        Encoding.Latin1.GetString(binary.Data).ShouldContain($"/CreationDate (D:{signed}+00'00')");

        // Printed on the patient's birthday the PDF says something else: the document is a new version.
        var older = Printed(data, "D:20261121093012+00'00'", "Age: 46");
        AbdmDocumentBuilder.Build(AbdmRecords.Prescription(older)).Meta.VersionId
            .ShouldNotBe(AbdmDocumentBuilder.Build(AbdmRecords.Prescription(first)).Meta.VersionId);
    }

    [Theory]
    [InlineData("/CreationDate (D:20261021005544+05'30')", "/CreationDate (D:20260925033500+00'00')")]
    [InlineData("/ModDate (D:20261021005544Z)", "/ModDate (D:20260925033500Z)")]
    [InlineData("/ModDate (D:20261021005544)", "/ModDate (D:20260925033500)")]
    [InlineData("/Title (D:20261021005544)", "/Title (D:20261021005544)")]
    public void A_pdfs_dates_become_the_documents_in_utc_and_nothing_else_moves(string original, string expected)
    {
        var date = new DateTimeOffset(2026, 9, 25, 9, 5, 0, TimeSpan.FromHours(5.5));
        byte[] binary = [0x00, 0x80, 0xE2, 0xFF];
        var pdf = Encoding.Latin1.GetBytes($"%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n<<{original}>>\n").Concat(binary).ToArray();

        var dated = AbdmDocumentBuilder.PdfAsOf(pdf, date);

        dated.Length.ShouldBe(pdf.Length, "the cross-reference offsets after the date must still hold");
        Encoding.Latin1.GetString(dated).ShouldContain(expected);
        dated[^4..].ShouldBe(binary);
    }

    [Fact]
    public void A_doctor_the_document_leaves_out_does_not_change_its_version()
    {
        // The consultation names the cardiologist it referred to in its text, but holds no resource for him: a change to his
        // master record leaves the document as it was.
        var data = AbdmTestData.ConsultationRecord();
        var edited = data with
        {
            Context = data.Context with
            {
                Practitioners = [.. data.Context.Practitioners.Select(p => p.Id == AbdmTestData.CardiologistId ? p with { UpdatedAt = TestData.Now.AddMinutes(-1) } : p)],
            },
        };

        Json(AbdmRecords.Consultation(edited)).ShouldBe(Json(AbdmRecords.Consultation(data)));
    }

    [Fact]
    public void A_document_does_not_depend_on_the_order_its_rows_were_read_in()
    {
        var consultation = AbdmTestData.ConsultationRecord();
        consultation = consultation with
        {
            Diagnoses = [.. consultation.Diagnoses, consultation.Diagnoses[1] with { Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ae020"), Title = "Dengue fever" }],
            Allergies = [.. consultation.Allergies, Allergy("Sulfonamides", consultation.Allergies[0].RecordedAt)],
        };
        Json(AbdmRecords.Consultation(Reversed(consultation))).ShouldBe(Json(AbdmRecords.Consultation(consultation)));

        var summary = AbdmTestData.DischargeSummaryRecord(pdf: null);
        Json(AbdmRecords.DischargeSummary(summary with
        {
            Context = Reversed(summary.Context),
            Diagnoses = [.. summary.Diagnoses.Reverse()],
            Lines = [.. summary.Lines.Reverse()],
            Allergies = [.. summary.Allergies.Reverse()],
            LabReports = [.. summary.LabReports.Reverse()],
            LabResults = [.. summary.LabResults.Reverse()],
        })).ShouldBe(Json(AbdmRecords.DischargeSummary(summary)));

        var report = AbdmTestData.LabReportRecord(AbdmTestData.Pdf);
        Json(AbdmRecords.LabReport(report with { Context = Reversed(report.Context), Results = [.. report.Results.Reverse()] }))
            .ShouldBe(Json(AbdmRecords.LabReport(report)));

        var prescription = AbdmTestData.Prescription(AbdmTestData.Pdf);
        Json(AbdmRecords.Prescription(prescription with { Context = Reversed(prescription.Context), Lines = [.. prescription.Lines.Reverse()] }))
            .ShouldBe(Json(AbdmRecords.Prescription(prescription)));
    }

    [Fact]
    public async Task Without_a_browser_every_document_goes_without_its_pdf()
    {
        var source = new PrintedCopy();

        (await AbdmPdfSource.CreateAsync(new Renderer(new PdfRendererStatus(false, null, "No browser")), source, Guid.NewGuid(), TestContext.Current.CancellationToken))
            .ShouldBeNull();
        source.Made.ShouldBe(0);
    }

    [Fact]
    public async Task A_pdf_that_cannot_be_made_now_fails_the_export_rather_than_leaving_the_pdf_out()
    {
        // The browser is there, but the last PDF took too long: this one is still made, and if it fails the export fails
        // (503, to be tried again) rather than going out as a different document without it.
        var renderer = new Renderer(new PdfRendererStatus(false, @"C:\Program Files\Microsoft\Edge\Application\msedge.exe", "Making the PDF took too long."));
        var ct = TestContext.Current.CancellationToken;

        (await AbdmPdfSource.CreateAsync(renderer, new PrintedCopy(), Guid.NewGuid(), ct)).ShouldNotBeNull().Content.ShouldBe(AbdmTestData.Pdf.Content);
        await Should.ThrowAsync<PdfRenderException>(() => AbdmPdfSource.CreateAsync(renderer, new PrintedCopy(fails: true), Guid.NewGuid(), ct));
    }

    private static string Json(AbdmDocument document) => FhirJson.Serialize(AbdmDocumentBuilder.Build(document));

    private static AllergyRow Allergy(string allergen, DateTimeOffset recorded) => new()
    {
        Id = AbdmDocumentBuilderIds.For(allergen),
        PatientId = TestData.PatientId,
        Allergen = allergen,
        Type = AllergyType.Drug,
        Severity = AllergySeverity.Moderate,
        Status = AllergyStatus.Active,
        RecordedAt = recorded,
        CreatedAt = recorded,
    };

    /// <summary>The prescription with its printed PDF made at <paramref name="made"/>, saying <paramref name="age"/>.</summary>
    private static PrescriptionData Printed(PrescriptionData data, string made, string age) => data with
    {
        Context = data.Context with
        {
            Pdf = new AbdmPdf(Encoding.Latin1.GetBytes(
                $"%PDF-1.4\n1 0 obj<</Producer (Skia/PDF m140)/CreationDate ({made})/ModDate ({made})>>endobj\n"
                + $"2 0 obj<</Length 12>>stream\n({age}) Tj\nendstream endobj\ntrailer<</Info 1 0 R>>\n%%EOF\n")),
        },
    };

    private static ConsultationData Reversed(ConsultationData d) => d with
    {
        Context = Reversed(d.Context),
        Diagnoses = [.. d.Diagnoses.Reverse()],
        Vitals = [.. d.Vitals.Reverse()],
        Lines = [.. d.Lines.Reverse()],
        Allergies = [.. d.Allergies.Reverse()],
    };

    private static AbdmContext Reversed(AbdmContext context) => context with
    {
        Practitioners = [.. context.Practitioners.Reverse()],
        Facilities = [.. context.Facilities.Reverse()],
        ReferralLabs = [.. context.ReferralLabs.Reverse()],
    };

    private sealed class Renderer(PdfRendererStatus status) : IPdfRenderer
    {
        public PdfRendererStatus Status { get; } = status;

        public Task<byte[]> RenderAsync(PdfSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A record's printed copy, or a PDF that cannot be made now.</summary>
    private sealed class PrintedCopy(bool fails = false) : INotificationDocumentSource
    {
        public int Made { get; private set; }

        public string Kind => "clinical.prescription";

        public Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken)
        {
            Made++;
            return fails
                ? throw new PdfRenderException("Making the PDF took too long.")
                : Task.FromResult<NotificationFile?>(new NotificationFile("prescription.pdf", AbdmTestData.Pdf.Content, "application/pdf"));
        }
    }
}

/// <summary>Stable ids for the rows these tests add, from their names.</summary>
internal static class AbdmDocumentBuilderIds
{
    public static Guid For(string name) => Guid.Parse(AbdmDocumentBuilder.Uuid("test-row/" + name));
}
