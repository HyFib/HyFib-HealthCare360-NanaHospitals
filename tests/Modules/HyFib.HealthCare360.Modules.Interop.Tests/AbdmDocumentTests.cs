using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// The ABDM documents (SRS EHR-08) against the NRCeS "FHIR Implementation Guide for ABDM": the DocumentBundle, the
/// Composition profile of each record type with the SNOMED CT codes the IG fixes, the resources' profiles and the
/// rules the IG adds to them, and references that all resolve inside the document. The four sample documents are kept in
/// Fixtures/Abdm and must match what the code builds; they were checked with the HL7 FHIR validator against the IG
/// (docs/modules/interop.md, ABDM health records). Set HC360_WRITE_ABDM_FIXTURES=1 to write them again after a change.
/// </summary>
public sealed class AbdmDocumentTests
{
    private static R4.Bundle Build(AbdmDocument document) => AbdmDocumentBuilder.Build(document, TestData.Now);

    private static readonly string[] Samples = ["prescription", "op-consultation", "lab-report", "discharge-summary"];

    public static TheoryData<string> RecordTypes => new(Samples);

    private static R4.Bundle Sample(string name) => name switch
    {
        "prescription" => Build(AbdmRecords.Prescription(AbdmTestData.Prescription(AbdmTestData.Pdf))),
        "op-consultation" => Build(AbdmRecords.Consultation(AbdmTestData.ConsultationRecord())),
        "lab-report" => Build(AbdmRecords.LabReport(AbdmTestData.LabReportRecord(AbdmTestData.Pdf))),
        _ => Build(AbdmRecords.DischargeSummary(AbdmTestData.DischargeSummaryRecord(pdf: null))),
    };

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public void Every_document_is_an_ig_document_bundle_led_by_its_composition(string name)
    {
        var bundle = Sample(name);

        bundle.Type.ShouldBe(R4.Bundle.BundleType.Document);
        bundle.Meta.Profile.ShouldBe([AbdmProfiles.DocumentBundle]);
        bundle.Meta.VersionId.ShouldNotBeNullOrWhiteSpace();
        bundle.Identifier.System.ShouldBe("urn:ietf:rfc:3986");
        bundle.Identifier.Value.ShouldBe($"urn:uuid:{bundle.Id}");
        bundle.Timestamp.ShouldBe(FhirTime.ToMilliseconds(TestData.Now));

        var composition = bundle.Entry[0].Resource.ShouldBeOfType<R4.Composition>();
        composition.Meta.VersionId.ShouldBe(bundle.Meta.VersionId);
        composition.Subject.Reference.ShouldBe(FullUrl<R4.Patient>(bundle));
        composition.Author.ShouldHaveSingleItem().Reference.ShouldNotBeNull();
        composition.Custodian.Reference.ShouldBe(FullUrl<R4.Organization>(bundle, o => o.Name == "Nana Hospitals, Pune"));
        composition.Text.Div.ShouldStartWith("<div xmlns=\"http://www.w3.org/1999/xhtml\">");
        composition.Section.ShouldAllBe(s => s.Code.Coding.Count == 1 && s.Text != null && (s.Entry.Count > 0 || s.Text.Div.Contains("<p>")));
    }

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public void Every_entry_has_a_uuid_full_url_and_every_reference_resolves_inside_the_document(string name)
    {
        var bundle = Sample(name);
        var fullUrls = bundle.Entry.Select(e => e.FullUrl).ToList();

        fullUrls.ShouldBeUnique();
        foreach (var entry in bundle.Entry)
        {
            entry.FullUrl.ShouldBe("urn:uuid:" + entry.Resource.Id);
            Guid.TryParse(entry.Resource.Id, out _).ShouldBeTrue();
        }

        foreach (var reference in References(bundle).Where(r => r.Reference is not null))
        {
            fullUrls.ShouldContain(reference.Reference, $"{name}: {reference.Reference} is not in the document");
        }

        // Every resource is used: the Composition reaches it through references.
        var referenced = References(bundle).Select(r => r.Reference).OfType<string>().ToHashSet();
        bundle.Entry.Skip(1).ShouldAllBe(e => referenced.Contains(e.FullUrl));
    }

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public void Every_resource_claims_its_ig_profile_and_has_a_narrative(string name)
    {
        foreach (var resource in Sample(name).Entry.Select(e => e.Resource))
        {
            resource.Meta.Profile.ShouldHaveSingleItem().ShouldStartWith(AbdmProfiles.Base, customMessage: $"{resource.TypeName} in {name}");
            if (resource is R4.DomainResource domain)
            {
                domain.Text.ShouldNotBeNull($"{resource.TypeName} in {name}").Div.ShouldContain("<p>");
            }
        }
    }

    [Theory]
    [InlineData("prescription", AbdmProfiles.PrescriptionRecord, "440545006", "Prescription record")]
    [InlineData("op-consultation", AbdmProfiles.OpConsultRecord, "371530004", "Clinical consultation report")]
    [InlineData("lab-report", AbdmProfiles.DiagnosticReportRecord, "721981007", "Diagnostic studies report")]
    [InlineData("discharge-summary", AbdmProfiles.DischargeSummaryRecord, "373942005", "Discharge summary")]
    public void The_composition_is_typed_with_the_snomed_code_the_ig_fixes(string name, string profile, string code, string display)
    {
        var composition = (R4.Composition)Sample(name).Entry[0].Resource;

        composition.Meta.Profile.ShouldBe([profile]);
        var coding = composition.Type.Coding.ShouldHaveSingleItem();
        coding.System.ShouldBe("http://snomed.info/sct");
        coding.Code.ShouldBe(code);
        coding.Display.ShouldBe(display);
        composition.Status.ShouldBe(R4.CompositionStatus.Final);
    }

    [Fact]
    public void The_patient_carries_the_abha_number_typed_as_abha_and_the_uhid()
    {
        var patient = Sample("prescription").Entry.Select(e => e.Resource).OfType<R4.Patient>().Single();

        var abha = patient.Identifier.Single(i => i.System == "https://healthid.ndhm.gov.in");
        abha.Value.ShouldBe("91-1234-5678-9010");
        abha.Type.Coding.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.System.ShouldBe("https://nrces.in/ndhm/fhir/r4/CodeSystem/ndhm-identifier-type-code"),
            c => c.Code.ShouldBe("ABHA"),
            c => c.Display.ShouldBe("Ayushman Bharat Health Account (ABHA) ID"));
        patient.Identifier.Single(i => i.System == FhirSystems.Uhid).Type.Coding[0].Code.ShouldBe("MR");
        patient.Name[0].Text.ShouldBe("Meera Anil Kulkarni");
    }

    [Fact]
    public void Doctors_and_the_hospital_have_the_typed_identifiers_the_ig_requires()
    {
        var bundle = Sample("prescription");

        var doctor = bundle.Entry.Select(e => e.Resource).OfType<R4.Practitioner>().ShouldHaveSingleItem();
        doctor.Name[0].Text.ShouldBe("Dr. Asha Rao");
        var registration = doctor.Identifier[0];
        registration.Type.Coding[0].Code.ShouldBe("MD");
        registration.Value.ShouldBe("MMC-2009-041234");
        registration.Assigner.Display.ShouldBe("Maharashtra Medical Council");
        doctor.Identifier.ShouldAllBe(i => i.System != null && i.Type.Coding[0].Display != null);
        doctor.Telecom.ShouldBeEmpty("a doctor's own phone and e-mail stay out of patients' documents");

        var hospital = bundle.Entry.Select(e => e.Resource).OfType<R4.Organization>().ShouldHaveSingleItem();
        hospital.Identifier[0].System.ShouldBe(FhirSystems.Hfr);
        hospital.Identifier[0].Value.ShouldBe("IN2710000123");
        hospital.Identifier[0].Type.Coding[0].Code.ShouldBe("PRN");
        hospital.Type[0].Coding[0].Code.ShouldBe("prov");
    }

    [Fact]
    public void A_prescription_lists_its_medicines_and_the_printed_pdf_as_typed_entries()
    {
        var bundle = Sample("prescription");
        var composition = (R4.Composition)bundle.Entry[0].Resource;

        var section = composition.Section.ShouldHaveSingleItem();
        section.Code.Coding[0].Code.ShouldBe("440545006");
        section.Entry.Select(e => e.Type).ShouldBe(["MedicationRequest", "MedicationRequest", "Binary"]);
        section.Text.Div.ShouldContain("Advice: Drink plenty of fluids and rest.");

        var requests = bundle.Entry.Select(e => e.Resource).OfType<R4.MedicationRequest>().ToList();
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(r => r.AuthoredOn != null && r.Requester.Reference == composition.Author[0].Reference && r.DosageInstruction.Count == 1);
        requests[0].Encounter.Reference.ShouldBe(composition.Encounter.Reference);

        var pdf = bundle.Entry.Select(e => e.Resource).OfType<R4.Binary>().ShouldHaveSingleItem();
        pdf.ContentType.ShouldBe("application/pdf");
        pdf.Data.ShouldBe(AbdmTestData.Pdf.Content);
        composition.Attester.ShouldHaveSingleItem().Mode.ShouldBe(R4.Composition.CompositionAttestationMode.Legal);
    }

    [Fact]
    public void A_prescription_without_a_pdf_lists_its_medicines_only()
    {
        var section = ((R4.Composition)Build(AbdmRecords.Prescription(AbdmTestData.Prescription(pdf: null))).Entry[0].Resource).Section[0];

        section.Entry.Select(e => e.Type).ShouldBe(["MedicationRequest", "MedicationRequest"]);
    }

    [Fact]
    public void A_consultation_has_the_ig_sections_with_the_notes_vitals_diagnoses_and_medicines()
    {
        var bundle = Sample("op-consultation");
        var composition = (R4.Composition)bundle.Entry[0].Resource;

        composition.Section.Select(s => s.Code.Coding[0].Code).ShouldBe(
            ["422843007", "371529009", "425044008", "722446000", "721912009", "390906007", "306206005", "51847-2"]);
        composition.Encounter.Reference.ShouldBe(FullUrl<R4.Encounter>(bundle));
        composition.Section[0].Text.Div.ShouldContain("Chief complaint: Fever for three days");
        composition.Section[6].Text.Div.ShouldContain("Referred to Dr. Vikram Shah: Palpitations on exertion");
        composition.Section[7].Text.Div.ShouldContain("Assessment: Acute febrile illness, probably viral.");

        var conditions = bundle.Entry.Select(e => e.Resource).OfType<R4.Condition>().ToList();
        conditions.Count.ShouldBe(2);
        conditions[0].Code.Coding.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.System.ShouldBe("http://hl7.org/fhir/sid/icd-10"), c => c.Code.ShouldBe("R50.9"), c => c.Display.ShouldBe("Fever, unspecified"));
        conditions[1].Code.Coding.ShouldBeEmpty();
        conditions[1].Code.Text.ShouldBe("Viral fever");

        var observations = bundle.Entry.Select(e => e.Resource).OfType<R4.Observation>().ToList();
        observations.Single(o => o.Code.Coding[0].Code == "85354-9").Meta.Profile.ShouldBe([AbdmProfiles.ObservationVitalSigns]);
        observations.Single(o => o.Code.Coding[0].Code == "29463-7").Meta.Profile.ShouldBe([AbdmProfiles.ObservationBodyMeasurement]);
        observations.ShouldAllBe(o => o.Encounter.Reference == composition.Encounter.Reference);
        observations.ShouldAllBe(o => o.Performer.Count == 1 && o.Performer[0].Reference == composition.Custodian.Reference);

        // An allergy recorded in error is not shared.
        bundle.Entry.Select(e => e.Resource).OfType<R4.AllergyIntolerance>().ShouldHaveSingleItem().Code.Text.ShouldBe("Penicillin");

        // The referral names a doctor who is not otherwise in the document: only the doctor who wrote it is.
        bundle.Entry.Select(e => e.Resource).OfType<R4.Practitioner>().ShouldHaveSingleItem().Name[0].Text.ShouldBe("Dr. Asha Rao");
    }

    [Fact]
    public void A_lab_report_is_interpreted_by_the_laboratory_with_snomed_section_and_loinc_codes_only()
    {
        var bundle = Sample("lab-report");
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        var laboratory = FullUrl<R4.Organization>(bundle);

        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(laboratory);
        var section = composition.Section.ShouldHaveSingleItem();
        section.Code.Coding[0].Code.ShouldBe("4241000179101");
        section.Entry.Select(e => e.Type).ShouldBe(["DiagnosticReport", "DocumentReference"]);

        var report = bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().ShouldHaveSingleItem();
        report.Meta.Profile.ShouldBe([AbdmProfiles.DiagnosticReportLab]);
        report.Category.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().System.ShouldBe("http://snomed.info/sct");
        report.Code.Coding.ShouldAllBe(c => c.System == "http://loinc.org" && c.Display != null);
        report.ResultsInterpreter.ShouldHaveSingleItem().Reference.ShouldBe(laboratory);
        report.Result.Count.ShouldBe(2);
        report.Conclusion.ShouldBe("Flagged by the laboratory: Haemoglobin (Low), C-reactive protein (High).");

        var results = bundle.Entry.Select(e => e.Resource).OfType<R4.Observation>().ToList();
        results.Count.ShouldBe(2);
        results.SelectMany(o => o.Code.Coding).ShouldAllBe(c => c.System == "http://loinc.org" && !string.IsNullOrEmpty(c.Display));
        results.ShouldAllBe(o => o.Meta.Profile.Single() == AbdmProfiles.Observation);

        var pdf = bundle.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().ShouldHaveSingleItem();
        pdf.Content.ShouldHaveSingleItem().Attachment.ContentType.ShouldBe("application/pdf");
        pdf.Author.ShouldHaveSingleItem().Reference.ShouldBe(laboratory);
    }

    [Fact]
    public void A_lab_report_that_flags_nothing_does_not_call_its_results_normal()
    {
        // A qualitative result ("Reactive"), or one with no reference range, is never flagged, so an unflagged report is
        // not a normal one: the conclusion the IG requires only points to the results.
        var data = AbdmTestData.LabReportRecord(pdf: null);
        var bundle = Build(AbdmRecords.LabReport(data with { Results = [.. data.Results.Select(r => r with { Flag = null })] }));

        var report = bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().ShouldHaveSingleItem();
        report.Conclusion.ShouldBe("See the individual results.");
        report.Text.Div.ShouldNotContain("See the individual results");
        FhirJson.Serialize(bundle).ShouldNotContain("reference range", Case.Insensitive);
    }

    [Fact]
    public void A_discharge_summary_has_the_stay_diagnoses_reports_medicines_and_the_summary_text_without_a_pdf()
    {
        var bundle = Sample("discharge-summary");
        var composition = (R4.Composition)bundle.Entry[0].Resource;

        composition.Section.Select(s => s.Code.Coding[0].Code).ShouldBe(
            ["422843007", "722446000", "721981007", "1003606003", "1003640003", "734163000", "373942005", "8648-8"]);
        var stay = bundle.Entry.Select(e => e.Resource).OfType<R4.Encounter>().ShouldHaveSingleItem();
        stay.Class.Code.ShouldBe("IMP");
        composition.Encounter.Reference.ShouldBe(FullUrl<R4.Encounter>(bundle));
        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl<R4.Practitioner>(bundle, p => p.Name[0].Text == "Dr. Vikram Shah"));

        bundle.Entry.Select(e => e.Resource).OfType<R4.Condition>().ShouldHaveSingleItem().Code.Coding[0].Code.ShouldBe("I21.4");
        var report = bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().ShouldHaveSingleItem();
        report.Conclusion.ShouldBe("Flagged by the laboratory: Troponin I (Critical high).");
        bundle.Entry.Select(e => e.Resource).OfType<R4.MedicationRequest>().ShouldHaveSingleItem().Category[0].Coding[0].Code.ShouldBe("inpatient");

        // Without a PDF the summary travels as its text, so the document still carries it whole.
        var summary = bundle.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().ShouldHaveSingleItem();
        var attachment = summary.Content.ShouldHaveSingleItem().Attachment;
        attachment.ContentType.ShouldBe("text/plain; charset=utf-8");
        System.Text.Encoding.UTF8.GetString(attachment.Data).ShouldContain("Course in hospital: Managed medically in the CCU.");
        composition.Section[7].Text.Div.ShouldContain("Condition at discharge: Stable, pain free.");
    }

    [Fact]
    public void A_discharge_summary_with_a_pdf_attaches_the_pdf()
    {
        var bundle = Build(AbdmRecords.DischargeSummary(AbdmTestData.DischargeSummaryRecord(AbdmTestData.Pdf)));

        bundle.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().Single().Content.ShouldHaveSingleItem().Attachment.ShouldSatisfyAllConditions(
            a => a.ContentType.ShouldBe("application/pdf"),
            a => a.Data.ShouldBe(AbdmTestData.Pdf.Content));
    }

    [Fact]
    public void An_unsigned_doctor_leaves_the_hospital_as_the_author_of_a_discharge_summary()
    {
        var data = AbdmTestData.DischargeSummaryRecord(pdf: null);
        var composition = (R4.Composition)Build(AbdmRecords.DischargeSummary(data with
        {
            Summary = data.Summary with { PractitionerId = null, SignedByPractitionerId = null },
        })).Entry[0].Resource;

        composition.Author.ShouldHaveSingleItem().Type.ShouldBe("Organization");
        composition.Attester.ShouldBeEmpty();
    }

    [Fact]
    public void An_amended_record_is_an_amended_composition_with_a_new_document_identifier()
    {
        var data = AbdmTestData.DischargeSummaryRecord(pdf: null);
        var original = Build(AbdmRecords.DischargeSummary(data));
        var amended = Build(AbdmRecords.DischargeSummary(data with
        {
            Summary = data.Summary with
            {
                Status = Clinical.Domain.DischargeSummaryStatus.Amended,
                AmendedAt = TestData.Now.AddDays(-1),
                UpdatedAt = TestData.Now.AddDays(-1),
            },
        }));

        ((R4.Composition)amended.Entry[0].Resource).Status.ShouldBe(R4.CompositionStatus.Amended);
        amended.Identifier.Value.ShouldNotBe(original.Identifier.Value, "a new version of the document is a new document");
        amended.Entry[0].Resource.Id.ShouldBe(original.Entry[0].Resource.Id, "the composition keeps its id across versions");
        amended.Entry.Skip(1).Select(e => e.FullUrl).ShouldBe(original.Entry.Skip(1).Select(e => e.FullUrl));
    }

    [Fact]
    public void A_record_exported_again_after_its_patient_or_doctor_changed_is_a_new_version_of_the_document()
    {
        var data = AbdmTestData.Prescription(AbdmTestData.Pdf);
        var original = Build(AbdmRecords.Prescription(data));

        // Front office links the patient's ABHA number after the first export: only the patient changed.
        var linked = Build(AbdmRecords.Prescription(data with
        {
            Context = data.Context with { Patient = data.Context.Patient with { UpdatedAt = TestData.Now.AddMinutes(-5) } },
        }));
        linked.Meta.VersionId.ShouldNotBe(original.Meta.VersionId);
        linked.Meta.LastUpdated.ShouldBe(TestData.Now.AddMinutes(-5));
        linked.Identifier.Value.ShouldNotBe(original.Identifier.Value, "a receiver that keeps one copy per identifier must not keep the stale one");
        ((R4.Composition)linked.Entry[0].Resource).Meta.VersionId.ShouldBe(linked.Meta.VersionId);

        // The doctor's registration is corrected in the master.
        var corrected = Build(AbdmRecords.Prescription(data with
        {
            Context = data.Context with
            {
                Practitioners = [.. data.Context.Practitioners.Select(p => p with { UpdatedAt = TestData.Now.AddMinutes(-1) })],
            },
        }));
        corrected.Meta.VersionId.ShouldNotBe(original.Meta.VersionId);
        corrected.Entry.Select(e => e.Resource).OfType<R4.Practitioner>().ShouldHaveSingleItem().Meta.LastUpdated
            .ShouldBe(TestData.Now.AddMinutes(-1));
    }

    [Fact]
    public void A_document_says_what_its_record_said_whenever_it_is_exported()
    {
        // Three days of paracetamol and five of cetirizine, from the consultation on: exported a month later, when both
        // courses are over, the prescription still shows the medicines as the doctor signed them, and is the same document.
        var data = AbdmTestData.Prescription(AbdmTestData.Pdf);
        var first = AbdmDocumentBuilder.Build(AbdmRecords.Prescription(data), TestData.Now);
        var later = AbdmDocumentBuilder.Build(AbdmRecords.Prescription(data), TestData.Now.AddMonths(1));

        (later.Id, later.Identifier.Value, later.Meta.VersionId).ShouldBe((first.Id, first.Identifier.Value, first.Meta.VersionId));
        later.Entry.Select(e => e.Resource).OfType<R4.MedicationRequest>().Select(m => m.Status)
            .ShouldBe([R4.MedicationRequest.MedicationrequestStatus.Active, R4.MedicationRequest.MedicationrequestStatus.Active]);
    }

    [Fact]
    public void A_document_that_loses_an_entry_is_a_new_version_though_nothing_in_it_changed_later()
    {
        // A sulfonamide allergy recorded yesterday; then the penicillin allergy of a year ago is marked as recorded in error.
        // The latest change in the document is still yesterday's, but a receiver must not keep the copy with penicillin.
        var data = AbdmTestData.DischargeSummaryRecord(pdf: null);
        var penicillin = data.Allergies[0];
        var sulfonamides = penicillin with
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac022"),
            Allergen = "Sulfonamides",
            RecordedAt = TestData.Now.AddDays(-1),
            CreatedAt = TestData.Now.AddDays(-1),
        };
        var before = Build(AbdmRecords.DischargeSummary(data with { Allergies = [penicillin, sulfonamides] }));
        var after = Build(AbdmRecords.DischargeSummary(data with { Allergies = [penicillin with { Status = AllergyStatus.EnteredInError }, sulfonamides] }));

        after.Entry.Select(e => e.Resource).OfType<R4.AllergyIntolerance>().ShouldHaveSingleItem().Code.Text.ShouldBe("Sulfonamides");
        after.Meta.LastUpdated.ShouldBe(before.Meta.LastUpdated);
        after.Meta.VersionId.ShouldNotBe(before.Meta.VersionId);
        after.Identifier.Value.ShouldNotBe(before.Identifier.Value);
        ((R4.Composition)after.Entry[0].Resource).Meta.VersionId.ShouldBe(after.Meta.VersionId);
    }

    [Fact]
    public void A_documents_version_leaves_out_the_pdf_which_is_made_again_at_every_export()
    {
        var data = AbdmTestData.Prescription(AbdmTestData.Pdf);
        var reprinted = data with { Context = data.Context with { Pdf = new AbdmPdf([.. AbdmTestData.Pdf.Content, .. "% printed again"u8]) } };

        Build(AbdmRecords.Prescription(reprinted)).Meta.VersionId.ShouldBe(Build(AbdmRecords.Prescription(data)).Meta.VersionId);
    }

    [Fact]
    public void A_sections_narrative_reads_whole_with_its_free_text_and_then_its_entries()
    {
        var composition = (R4.Composition)Sample("op-consultation").Entry[0].Resource;

        // The doctor's own words, then the diagnoses the section lists: more than the entries say, so "additional".
        var complaints = composition.Section[0].Text;
        complaints.Status.ShouldBe(R4.Narrative.NarrativeStatus.Additional);
        complaints.Div.ShouldContain("<p>Chief complaint: Fever for three days</p><p>Diagnoses:</p><p>Fever, unspecified</p><p>Viral fever</p>");
        composition.Section[2].Text.Div.ShouldSatisfyAllConditions(
            div => div.ShouldContain("<p>Examination: "), div => div.ShouldContain("<p>Vital signs:</p>"), div => div.ShouldContain("<p>Heart rate: 96 beats/minute</p>"));

        // A section of entries alone is generated from them.
        composition.Section[3].Text.Status.ShouldBe(R4.Narrative.NarrativeStatus.Generated);
        composition.Section[3].Text.Div.ShouldBe("<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Allergy Penicillin</p></div>");

        // A prescription: the advice, then the medicines.
        var prescription = ((R4.Composition)Sample("prescription").Entry[0].Resource).Section[0].Text.Div;
        prescription.ShouldContain("<p>Advice: Drink plenty of fluids and rest.</p><p>Medicines:</p><p>Dolo 650 Tablet (Paracetamol):");
    }

    [Fact]
    public void A_discharge_summary_lists_the_stays_medicines_under_the_doctors_list_of_medicines_on_discharge()
    {
        var medications = ((R4.Composition)Sample("discharge-summary").Entry[0].Resource).Section[3].Text;

        medications.Status.ShouldBe(R4.Narrative.NarrativeStatus.Additional);
        medications.Div.ShouldContain("<p>Medications on discharge: ");
        medications.Div.ShouldContain("<p>Prescribed during the stay:</p><p>Atorvastatin 40 mg Tablet:");
    }

    [Fact]
    public void An_amended_note_is_written_by_both_doctors_and_attested_by_the_one_who_amended_it()
    {
        var data = AbdmTestData.ConsultationRecord();
        var amendedAt = TestData.Now.AddHours(-1);
        var composition = (R4.Composition)Build(AbdmRecords.Consultation(data with
        {
            Note = data.Note with { Status = EncounterStatus.Amended, AmendedAt = amendedAt, UpdatedAt = amendedAt, AmendedByPractitionerId = AbdmTestData.CardiologistId },
        })).Entry[0].Resource;

        composition.Status.ShouldBe(R4.CompositionStatus.Amended);
        composition.Author.Select(a => a.Display).ShouldBe(["Dr. Asha Rao", "Dr. Vikram Shah"]);
        var attester = composition.Attester.ShouldHaveSingleItem();
        (attester.Mode, attester.Party.Display, attester.Time).ShouldBe((R4.Composition.CompositionAttestationMode.Legal, "Dr. Vikram Shah", FhirTime.DateTime(amendedAt)));
    }

    [Fact]
    public void An_amended_record_whose_amender_is_not_a_doctor_names_no_one_as_answering_for_it()
    {
        // The doctor who signed attested what they signed, not what someone changed afterwards.
        var data = AbdmTestData.DischargeSummaryRecord(pdf: null);
        var amended = data with { Summary = data.Summary with { Status = DischargeSummaryStatus.Amended, AmendedAt = TestData.Now.AddHours(-1) } };

        var composition = (R4.Composition)Build(AbdmRecords.DischargeSummary(amended)).Entry[0].Resource;
        composition.Attester.ShouldBeEmpty();
        composition.Author.ShouldHaveSingleItem().Display.ShouldBe("Dr. Vikram Shah");

        var byDoctor = (R4.Composition)Build(AbdmRecords.DischargeSummary(amended with { AmendedByPractitionerId = TestData.DoctorId })).Entry[0].Resource;
        byDoctor.Attester.ShouldHaveSingleItem().Party.Display.ShouldBe("Dr. Asha Rao");
        byDoctor.Author.Select(a => a.Display).ShouldBe(["Dr. Vikram Shah", "Dr. Asha Rao"]);
    }

    [Fact]
    public void A_lab_report_released_before_all_its_results_is_a_preliminary_document()
    {
        var data = AbdmTestData.LabReportRecord(AbdmTestData.Pdf);
        var bundle = Build(AbdmRecords.LabReport(data with { Report = data.Report with { IsPartial = true } }));

        ((R4.Composition)bundle.Entry[0].Resource).Status.ShouldBe(R4.CompositionStatus.Preliminary);
        bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().Single().Status.ShouldBe(R4.DiagnosticReport.DiagnosticReportStatus.Partial);
        bundle.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().Single().DocStatus.ShouldBe(R4.CompositionStatus.Preliminary);
    }

    [Fact]
    public void A_lab_code_goes_with_its_loinc_name_or_not_at_all()
    {
        // The laboratory entered the LOINC name of haemoglobin's code, not of CRP's: CRP goes by the test's own name.
        var data = AbdmTestData.LabReportRecord(pdf: null);
        var bundle = Build(AbdmRecords.LabReport(data with { Results = [data.Results[0], data.Results[1] with { LoincName = null }] }));

        var results = bundle.Entry.Select(e => e.Resource).OfType<R4.Observation>().ToList();
        results[0].Code.Coding.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.Code.ShouldBe("718-7"), c => c.Display.ShouldBe("Hemoglobin [Mass/volume] in Blood"));
        results[0].Code.Text.ShouldBe("Haemoglobin");
        results[1].Code.Coding.ShouldBeEmpty();
        results[1].Code.Text.ShouldBe("C-reactive protein");
    }

    [Fact]
    public void A_record_kept_under_a_merged_duplicate_uhid_is_about_the_documents_patient()
    {
        // The laboratory keeps a report under the UHID it was made for; merged into the patient's, the document is theirs.
        var duplicate = Guid.Parse("01923a4b-0000-7000-8000-0000000ab0ff");
        var data = AbdmTestData.LabReportRecord(AbdmTestData.Pdf);
        var bundle = Build(AbdmRecords.LabReport(data with
        {
            Report = data.Report with { PatientId = duplicate },
            Results = [.. data.Results.Select(r => r with { PatientId = duplicate })],
        }));

        var patient = FullUrl<R4.Patient>(bundle);
        bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().Single().Subject.Reference.ShouldBe(patient);
        bundle.Entry.Select(e => e.Resource).OfType<R4.Observation>().ShouldAllBe(o => o.Subject.Reference == patient);
    }

    [Fact]
    public void Exporting_the_same_record_again_gives_the_same_document()
    {
        var first = FhirJson.Serialize(Sample("op-consultation"));
        var second = FhirJson.Serialize(Sample("op-consultation"));

        second.ShouldBe(first);
    }

    [Fact]
    public void The_same_patient_has_the_same_id_in_every_document() =>
        Samples.Select(name => FullUrl<R4.Patient>(Sample(name))).Distinct().ShouldHaveSingleItem();

    [Fact]
    public void Document_ids_are_rfc_9562_version_8_uuids()
    {
        var uuid = Guid.Parse(AbdmDocumentBuilder.Uuid("Patient/x"));

        uuid.Version.ShouldBe(8);
        uuid.Variant.ShouldBeInRange(8, 11);
        AbdmDocumentBuilder.Uuid("Patient/x").ShouldBe(AbdmDocumentBuilder.Uuid("Patient/x"));
        AbdmDocumentBuilder.Uuid("Patient/y").ShouldNotBe(AbdmDocumentBuilder.Uuid("Patient/x"));
    }

    [Fact]
    public void Narratives_are_escaped_xhtml()
    {
        var narrative = AbdmNarrative.Generated(["<b>Fever</b> & chills", "second\nthird"]);

        narrative.Div.ShouldBe("<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>&lt;b&gt;Fever&lt;/b&gt; &amp; chills</p><p>second</p><p>third</p></div>");
        narrative.Status.ShouldBe(R4.Narrative.NarrativeStatus.Generated);
    }

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public void Every_document_round_trips_through_the_fhir_parser(string name)
    {
        var json = FhirJson.Serialize(Sample(name));

        var parsed = JsonSerializer.Deserialize<R4.Bundle>(json, FhirJson.Options(pretty: false)).ShouldNotBeNull();
        FhirJson.Serialize(parsed).ShouldBe(json);
    }

    /// <summary>
    /// The sample documents in Fixtures/Abdm are what the code builds today. They are the files checked with the HL7 FHIR
    /// validator; a change to the documents shows up here, and the samples are written again with
    /// HC360_WRITE_ABDM_FIXTURES=1 and validated again.
    /// </summary>
    [Theory]
    [MemberData(nameof(RecordTypes))]
    public void The_sample_documents_match_the_fixtures(string name)
    {
        var path = Path.Combine(FixtureDirectory(), $"{name}.json");
        var json = FhirJson.Serialize(Sample(name), pretty: true) + "\n";
        if (Environment.GetEnvironmentVariable("HC360_WRITE_ABDM_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        File.Exists(path).ShouldBeTrue($"Missing fixture {path}: run the tests with HC360_WRITE_ABDM_FIXTURES=1.");
        JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), JsonNode.Parse(json)).ShouldBeTrue(
            $"{name}.json differs from the document the code builds: write the fixtures again (HC360_WRITE_ABDM_FIXTURES=1) and validate them.");
    }

    /// <summary>
    /// Fixtures/Abdm in the source tree (written there with HC360_WRITE_ABDM_FIXTURES=1), found from the repository root
    /// above the test's output folder. Not from the caller's file path: CI builds are deterministic and map source paths
    /// to <c>/_/</c>, a folder that does not exist when the tests run (TestSourcePathTests, ArchitectureTests).
    /// </summary>
    private static string FixtureDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyFib.HealthCare360.slnx")))
            {
                return Path.Combine(directory.FullName, "tests", "Modules", "HyFib.HealthCare360.Modules.Interop.Tests", "Fixtures", "Abdm");
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test's folder.");
    }

    private static string FullUrl<T>(R4.Bundle bundle, Func<T, bool>? match = null)
        where T : R4.Resource =>
        bundle.Entry.Single(e => e.Resource is T resource && (match?.Invoke(resource) ?? true)).FullUrl!;

    private static IEnumerable<R4.ResourceReference> References(R4.Base element)
    {
        foreach (var (_, value) in element.EnumerateElements())
        {
            IEnumerable<R4.Base> children = value switch
            {
                R4.Base single => [single],
                IEnumerable<R4.Base> list => list,
                _ => [],
            };
            foreach (var child in children)
            {
                if (child is R4.ResourceReference reference)
                {
                    yield return reference;
                }

                foreach (var nested in References(child))
                {
                    yield return nested;
                }
            }
        }
    }
}
