using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Testing.Abdm;
using Modality = HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging.Modality;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// The ABDM documents (SRS EHR-08) against the NRCeS "FHIR Implementation Guide for ABDM": the DocumentBundle, the
/// Composition profile of each record type with the SNOMED CT codes the IG fixes, the resources' profiles and the
/// rules the IG adds to them, and references that all resolve inside the document. The five sample documents are kept in
/// Fixtures/Abdm and must match what the code builds; they are validated against the IG here (Firely), and were also
/// checked with the HL7 FHIR validator (docs/modules/interop.md, ABDM health records). Set HC360_WRITE_ABDM_FIXTURES=1
/// to write them again after a change.
/// </summary>
public sealed class AbdmDocumentTests
{
    private static R4.Bundle Build(AbdmDocument document) => AbdmDocumentBuilder.Build(document);

    private static readonly string[] Samples = ["prescription", "op-consultation", "lab-report", "radiology-report", "discharge-summary"];

    public static TheoryData<string> RecordTypes => new(Samples);

    private static R4.Bundle Sample(string name) => name switch
    {
        "prescription" => Build(AbdmRecords.Prescription(AbdmTestData.Prescription(AbdmTestData.Pdf))),
        "op-consultation" => Build(AbdmRecords.Consultation(AbdmTestData.ConsultationRecord())),
        "lab-report" => Build(AbdmRecords.LabReport(AbdmTestData.LabReportRecord(AbdmTestData.Pdf))),
        "radiology-report" => Build(AbdmRecords.RadiologyReport(AbdmTestData.RadiologyReportRecord(AbdmTestData.Pdf))),
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
        bundle.Timestamp.ShouldNotBeNull().ShouldBe(bundle.Meta.LastUpdated.ShouldNotBeNull(), "a version is assembled once: its time, not the export's");

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
    [InlineData("radiology-report", AbdmProfiles.DiagnosticReportRecord, "721981007", "Diagnostic studies report")]
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
    public void A_radiology_report_is_an_imaging_report_of_its_study_interpreted_by_its_radiologists()
    {
        var bundle = Sample("radiology-report");
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        var hospital = FullUrl<R4.Organization>(bundle);
        var radiologist = FullUrl<R4.Practitioner>(bundle, p => p.Name[0].Text == "Dr. Neha Joshi");
        var resident = FullUrl<R4.Practitioner>(bundle, p => p.Name[0].Text == "Dr. Kiran Patil");
        var data = AbdmTestData.RadiologyReport();

        // The radiologist who signed it off wrote the document and answers for it, from when she signed it off.
        composition.Title.ShouldBe("Radiology report");
        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(radiologist);
        var attester = composition.Attester.ShouldHaveSingleItem();
        (attester.Mode, attester.Party.Reference, attester.Time).ShouldBe((R4.Composition.CompositionAttestationMode.Legal, radiologist, FhirTime.DateTime(data.VerifiedAt!.Value)));
        composition.Date.ShouldBe(FhirTime.DateTime(data.ReleasedAt!.Value));
        var section = composition.Section.ShouldHaveSingleItem();
        section.Code.Coding.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.System.ShouldBe("http://snomed.info/sct"), c => c.Code.ShouldBe("4201000179104"), c => c.Display.ShouldBe("Imaging report"));
        section.Entry.Select(e => e.Type).ShouldBe(["DiagnosticReport", "DocumentReference"]);

        var report = bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().ShouldHaveSingleItem();
        report.Meta.Profile.ShouldBe([AbdmProfiles.DiagnosticReportImaging]);
        report.Status.ShouldBe(R4.DiagnosticReport.DiagnosticReportStatus.Final);
        report.Category.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.System.ShouldBe("http://snomed.info/sct"), c => c.Code.ShouldBe("394914008"), c => c.Display.ShouldBe("Radiology"));
        report.Code.Coding.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.System.ShouldBe("http://loinc.org"), c => c.Code.ShouldBe("18748-4"), c => c.Display.ShouldBe("Diagnostic imaging study"));
        report.Code.Text.ShouldBe("X-ray chest PA view");
        report.Identifier.ShouldHaveSingleItem().Value.ShouldBe("NHRD2600217");
        report.Issued.ShouldBe(data.ReleasedAt);

        // The hospital performed the study and issues the report; the resident who wrote it and the radiologist who
        // signed it off interpret it.
        report.Performer.ShouldHaveSingleItem().Reference.ShouldBe(hospital);
        report.ResultsInterpreter.Select(r => r.Reference).ShouldBe([resident, radiologist]);

        // Findings and impression: the impression as the conclusion, the whole report as text, with the study's modality
        // and body part as the printed report heads it.
        report.Conclusion.ShouldBe("No active lung lesion.");
        var text = System.Text.Encoding.UTF8.GetString(report.PresentedForm.ShouldHaveSingleItem().Data);
        text.ShouldStartWith("X-ray chest PA view\nModality: X-ray\nBody part: Chest\nTechnique: PA view, erect, in full inspiration.\n");
        text.ShouldContain("Findings: Both lung fields are clear.");
        text.ShouldContain("Impression: No active lung lesion.");

        // The study: its UID in PACS, its accession number, the modality as the worklist sends an X-ray, and its images.
        var study = bundle.Entry.Select(e => e.Resource).OfType<R4.ImagingStudy>().ShouldHaveSingleItem();
        report.ImagingStudy.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl<R4.ImagingStudy>(bundle));
        study.Meta.Profile.ShouldBe([AbdmProfiles.ImagingStudy]);
        study.Identifier.Select(i => (i.System, i.Value)).ShouldBe(
            [("urn:dicom:uid", "urn:oid:" + data.StudyInstanceUid), (FhirSystems.AccessionNumber, "NHRD2600217")]);
        study.Modality.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            c => c.System.ShouldBe("http://dicom.nema.org/resources/ontology/DCM"), c => c.Code.ShouldBe("CR"), c => c.Display.ShouldBe("Computed Radiography"));
        (study.NumberOfSeries, study.NumberOfInstances).ShouldBe((1, 2));
        study.Started.ShouldBe(FhirTime.DateTime(data.StartedAt!.Value));
        study.Subject.Reference.ShouldBe(FullUrl<R4.Patient>(bundle));
        study.Encounter.Reference.ShouldBe(composition.Encounter.Reference);
        study.Interpreter.Select(r => r.Reference).ShouldBe([resident, radiologist]);
        study.Description.ShouldBe("X-ray chest PA view");
        study.Text.Div.ShouldContain("<p>Body part: Chest</p>");

        // The printed report, as the laboratory's is.
        var pdf = bundle.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().ShouldHaveSingleItem();
        pdf.Type.Coding.ShouldHaveSingleItem().Code.ShouldBe("18748-4");
        pdf.Content.ShouldHaveSingleItem().Attachment.ShouldSatisfyAllConditions(
            a => a.ContentType.ShouldBe("application/pdf"), a => a.Data.ShouldBe(AbdmTestData.Pdf.Content));
        pdf.Author.ShouldHaveSingleItem().Reference.ShouldBe(hospital);
    }

    [Fact]
    public void A_radiology_report_says_it_has_no_key_images_rather_than_carrying_something_that_is_not_one()
    {
        // DiagnosticReportImaging requires a key image. HC360 keeps none (the images stay in PACS), so the one media entry
        // gives FHIR's reason for a required element the system cannot fill, and no Media resource stands in for one.
        var bundle = Sample("radiology-report");
        var media = bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().Single().Media.ShouldHaveSingleItem();

        media.Comment.ShouldBe("No key images are attached: the images of the study are kept in the hospital's PACS.");
        (media.Link.Reference, media.Link.Display).ShouldBe((null, "No key image"));
        var reason = media.Link.Extension.ShouldHaveSingleItem();
        reason.Url.ShouldBe("http://hl7.org/fhir/StructureDefinition/data-absent-reason");
        reason.Value.ShouldBeOfType<R4.Code>().Value.ShouldBe("unsupported");
        bundle.Entry.Select(e => e.Resource).OfType<R4.Media>().ShouldBeEmpty();
    }

    [Fact]
    public void An_addendum_after_the_release_amends_a_radiology_report_and_who_added_it_attests_it()
    {
        // The resident's second read finds an effusion an hour after the report was released.
        var data = AbdmTestData.RadiologyReportRecord(AbdmTestData.Pdf);
        var addedAt = data.Report.ReleasedAt!.Value.AddHours(1);
        var bundle = Build(AbdmRecords.RadiologyReport(data with
        {
            Report = data.Report with { Addenda = [new RadiologyAddendumRow("Small left pleural effusion on review.", "Second read", addedAt, AbdmTestData.ResidentUserId)] },
        }));
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        var radiologist = FullUrl<R4.Practitioner>(bundle, p => p.Name[0].Text == "Dr. Neha Joshi");
        var resident = FullUrl<R4.Practitioner>(bundle, p => p.Name[0].Text == "Dr. Kiran Patil");

        composition.Status.ShouldBe(R4.CompositionStatus.Amended);
        composition.Date.ShouldBe(FhirTime.DateTime(addedAt));
        composition.Author.Select(a => a.Reference).ShouldBe([radiologist, resident]);
        var attester = composition.Attester.ShouldHaveSingleItem();
        (attester.Party.Reference, attester.Time).ShouldBe((resident, FhirTime.DateTime(addedAt)));

        // The report is appended, and the addendum is in its conclusion, as it may correct the impression.
        var report = bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().Single();
        report.Status.ShouldBe(R4.DiagnosticReport.DiagnosticReportStatus.Appended);
        report.Issued.ShouldBe(addedAt);
        report.Conclusion.ShouldBe($"No active lung lesion. Addendum {FhirTime.DateTime(addedAt)}: Small left pleural effusion on review.");
        System.Text.Encoding.UTF8.GetString(report.PresentedForm[0].Data).ShouldContain("(reason: Second read): Small left pleural effusion on review.");
        bundle.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().Single().DocStatus.ShouldBe(R4.CompositionStatus.Amended);

        var validation = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);
        validation.Errors.ShouldBeEmpty(validation.Describe());
    }

    [Fact]
    public void An_addendum_before_the_release_is_part_of_the_report_as_first_released()
    {
        var data = AbdmTestData.RadiologyReportRecord(pdf: null);
        var addedAt = data.Report.VerifiedAt!.Value.AddMinutes(2);
        var composition = (R4.Composition)Build(AbdmRecords.RadiologyReport(data with
        {
            Report = data.Report with { Addenda = [new RadiologyAddendumRow("Old healed rib fracture on the left.", null, addedAt, AbdmTestData.RadiologistUserId)] },
        })).Entry[0].Resource;

        composition.Status.ShouldBe(R4.CompositionStatus.Final);
        composition.Date.ShouldBe(FhirTime.DateTime(data.Report.ReleasedAt!.Value));
        composition.Attester.ShouldHaveSingleItem().Time.ShouldBe(FhirTime.DateTime(data.Report.VerifiedAt.Value));
    }

    [Fact]
    public void A_radiology_report_of_users_who_are_not_doctors_is_written_and_interpreted_by_the_hospital()
    {
        // Signed off and amended by accounts not linked to a doctor: no one is named as a radiologist or attests it, and
        // the document still meets the profiles.
        var data = AbdmTestData.RadiologyReportRecord(pdf: null);
        var bundle = Build(AbdmRecords.RadiologyReport(data with
        {
            Doctors = new Dictionary<Guid, Guid>(),
            Report = data.Report with { Addenda = [new RadiologyAddendumRow("Image quality noted.", null, data.Report.ReleasedAt!.Value.AddHours(1), AbdmTestData.ClerkUserId)] },
        }));
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        var hospital = FullUrl<R4.Organization>(bundle);

        composition.Status.ShouldBe(R4.CompositionStatus.Amended);
        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(hospital);
        composition.Attester.ShouldBeEmpty();
        bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().Single().ResultsInterpreter.ShouldHaveSingleItem().Reference.ShouldBe(hospital);
        bundle.Entry.Select(e => e.Resource).OfType<R4.ImagingStudy>().Single().Interpreter.ShouldBeEmpty();
        bundle.Entry.Select(e => e.Resource).OfType<R4.Practitioner>().Select(p => p.Name[0].Text).ShouldBe(["Dr. Asha Rao"], "only the visit's doctor");

        var validation = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);
        validation.Errors.ShouldBeEmpty(validation.Describe());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.2.840.abc")]
    [InlineData("1.2..840")]
    public void A_study_whose_images_pacs_does_not_hold_names_no_uid_and_no_images(string? uid)
    {
        // Before PACS links the images the UID is only the one the worklist proposed, and the loader passes none; a UID
        // DICOM would not take is not passed on either.
        var data = AbdmTestData.RadiologyReportRecord(pdf: null);
        var study = Build(AbdmRecords.RadiologyReport(data with
        {
            Report = data.Report with { StudyInstanceUid = uid, SeriesCount = null, InstanceCount = null },
        })).Entry.Select(e => e.Resource).OfType<R4.ImagingStudy>().Single();

        study.Identifier.ShouldHaveSingleItem().System.ShouldBe(FhirSystems.AccessionNumber);
        (study.NumberOfSeries, study.NumberOfInstances).ShouldBe((null, null));
        study.Text.Div.ShouldNotContain("PACS");
    }

    [Theory]
    [InlineData(Modality.XR, "CR", "Computed Radiography", "X-ray")]
    [InlineData(Modality.DX, "DX", "Digital Radiography", "Digital X-ray")]
    [InlineData(Modality.CT, "CT", "Computed Tomography", "CT scan")]
    [InlineData(Modality.MR, "MR", "Magnetic Resonance", "MRI")]
    [InlineData(Modality.US, "US", "Ultrasound", "Ultrasound")]
    [InlineData(Modality.MG, "MG", "Mammography", "Mammography")]
    [InlineData(Modality.RF, "RF", "Radiofluoroscopy", "Fluoroscopy")]
    public void A_study_is_coded_with_the_dicom_modality_its_worklist_sends(Modality modality, string code, string display, string name)
    {
        var data = AbdmTestData.RadiologyReportRecord(pdf: null);
        var bundle = Build(AbdmRecords.RadiologyReport(data with { Report = data.Report with { Modality = modality } }));

        bundle.Entry.Select(e => e.Resource).OfType<R4.ImagingStudy>().Single().Modality.ShouldHaveSingleItem()
            .ShouldSatisfyAllConditions(c => c.Code.ShouldBe(code), c => c.Display.ShouldBe(display));
        System.Text.Encoding.UTF8.GetString(bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().Single().PresentedForm[0].Data)
            .ShouldContain($"Modality: {name}\n");
    }

    [Theory]
    [InlineData(DiagnosticReportStatus.Draft)]
    [InlineData(DiagnosticReportStatus.Reported)]
    [InlineData(DiagnosticReportStatus.Verified)]
    public void A_radiology_report_is_exported_only_once_released(DiagnosticReportStatus status)
    {
        // A draft, a report waiting for sign-off and one signed off but not released are still the radiologists' work.
        AbdmRecordLoader.RadiologyReportNotExportable(status).ShouldBe("Only a released radiology report can be exported.");
        AbdmRecordLoader.RadiologyReportNotExportable(DiagnosticReportStatus.Released).ShouldBeNull();
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
        // Three days of paracetamol and five of cetirizine, from the consultation on: exported again when both courses are
        // over, the prescription still shows the medicines as the doctor signed them, and is the same document, byte for
        // byte (its timestamp is its version's, not the export's).
        var data = AbdmTestData.Prescription(AbdmTestData.Pdf);
        var first = FhirJson.Serialize(Build(AbdmRecords.Prescription(data)));
        var later = Build(AbdmRecords.Prescription(data));

        FhirJson.Serialize(later).ShouldBe(first);
        later.Entry.Select(e => e.Resource).OfType<R4.MedicationRequest>().Select(m => m.Status)
            .ShouldBe([R4.MedicationRequest.MedicationrequestStatus.Active, R4.MedicationRequest.MedicationrequestStatus.Active]);
    }

    [Fact]
    public void A_document_that_loses_an_entry_is_a_new_version_though_nothing_in_it_changed_later()
    {
        // A sulfonamide allergy recorded during the stay; then the penicillin allergy of a year ago is marked as recorded in
        // error. Nothing in the document changed later, but a receiver must not keep the copy with penicillin.
        var data = AbdmTestData.DischargeSummaryRecord(pdf: null);
        var penicillin = data.Allergies[0];
        var duringTheStay = data.Summary.AdmittedAt!.Value.AddDays(1);
        var sulfonamides = penicillin with
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ac022"),
            Allergen = "Sulfonamides",
            RecordedAt = duringTheStay,
            CreatedAt = duringTheStay,
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
    public void A_sections_narrative_reads_whole_with_its_free_text_and_then_its_entries()
    {
        var composition = (R4.Composition)Sample("op-consultation").Entry[0].Resource;

        // The doctor's own words, then the diagnoses the section lists: more than the entries say, so "additional".
        var complaints = composition.Section[0].Text;
        complaints.Status.ShouldBe(R4.Narrative.NarrativeStatus.Additional);
        complaints.Div.ShouldContain(
            "<p>Chief complaint: Fever for three days</p><p>Diagnoses:</p><p>Fever, unspecified (provisional)</p><p>Viral fever (differential)</p>");
        composition.Section[2].Text.Div.ShouldSatisfyAllConditions(
            div => div.ShouldContain("<p>Examination: "), div => div.ShouldContain("<p>Vital signs:</p>"), div => div.ShouldContain("<p>Heart rate: 96 beats/minute</p>"));

        // A section of entries alone is generated from them.
        composition.Section[3].Text.Status.ShouldBe(R4.Narrative.NarrativeStatus.Generated);
        composition.Section[3].Text.Div.ShouldBe("<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Allergy Penicillin (high criticality): Hives (severe)</p></div>");

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
            Context = data.Context with { DuplicateUhids = new Dictionary<Guid, string> { [duplicate] = "NH-000077" } },
            Report = data.Report with { PatientId = duplicate },
            Results = [.. data.Results.Select(r => r with { PatientId = duplicate })],
        }));

        var patient = FullUrl<R4.Patient>(bundle);
        bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().Single().Subject.Reference.ShouldBe(patient);
        bundle.Entry.Select(e => e.Resource).OfType<R4.Observation>().ShouldAllBe(o => o.Subject.Reference == patient);

        // Its printed PDF names the duplicate's UHID, which the patient carries as an old one of theirs.
        var uhids = bundle.Entry.Select(e => e.Resource).OfType<R4.Patient>().Single().Identifier.Where(i => i.System == FhirSystems.Uhid).ToList();
        uhids.Select(i => (i.Value, i.Use)).ShouldBe([(TestData.Patient().Uhid, R4.Identifier.IdentifierUse.Usual), ("NH-000077", R4.Identifier.IdentifierUse.Old)]);

        // A duplicate whose records the document does not hold is not named.
        Build(AbdmRecords.LabReport(data with { Context = data.Context with { DuplicateUhids = new Dictionary<Guid, string> { [duplicate] = "NH-000077" } } }))
            .Entry.Select(e => e.Resource).OfType<R4.Patient>().Single().Identifier.ShouldNotContain(i => i.Value == "NH-000077");
    }

    [Fact]
    public void A_diagnosis_or_allergy_reads_with_the_statuses_that_qualify_it()
    {
        // In FHIR the statuses change what a Condition or AllergyIntolerance means, so its line says them.
        var data = AbdmTestData.ConsultationRecord();
        var diagnosis = data.Diagnoses[0];
        var allergy = data.Allergies[0];
        var bundle = Build(AbdmRecords.Consultation(data with
        {
            Diagnoses =
            [
                diagnosis,
                diagnosis with { Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ad001"), IsPrimary = false, Title = "Asthma", IcdCode = null, Status = DiagnosisStatus.Chronic, Type = DiagnosisType.Final },
                diagnosis with { Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ad002"), IsPrimary = false, Title = "Typhoid", IcdCode = null, Status = DiagnosisStatus.Resolved, ResolvedOn = TestData.Today.AddDays(-30) },
                data.Diagnoses[1],
            ],
            Allergies =
            [
                allergy,
                allergy with { Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ad003"), Allergen = "Sulfonamides", Reaction = "Rash", Severity = AllergySeverity.Moderate },
                allergy with { Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ad004"), Allergen = "Aspirin", Reaction = null, Severity = AllergySeverity.Mild },
            ],
        }));
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        composition.Section[0].Text.Div.ShouldContain(
            "<p>Fever, unspecified (provisional)</p><p>Asthma (chronic)</p><p>Typhoid (provisional, resolved)</p><p>Viral fever (differential)</p>");
        composition.Section[3].Text.Div.ShouldContain(
            "<p>Allergy Penicillin (high criticality): Hives (severe)</p><p>Allergy Sulfonamides: Rash (moderate)</p><p>Allergy Aspirin</p>");
        bundle.Entry.Select(e => e.Resource).OfType<R4.Condition>().Single(c => c.Code.Text == "Viral fever").Text.Div
            .ShouldBe("<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Viral fever (differential)</p></div>");

        // An allergy no longer active says so.
        var inactive = Build(AbdmRecords.Consultation(data with { Allergies = [ChangeStatus(allergy, AllergyStatus.Inactive)] }));
        ((R4.Composition)inactive.Entry[0].Resource).Section[3].Text.Div.ShouldContain("<p>Allergy Penicillin (inactive, high criticality): Hives (severe)</p>");
    }

    [Fact]
    public void An_amended_discharge_summarys_text_is_authenticated_by_who_answers_for_the_amendment()
    {
        // The DocumentReference carries the amended text: the doctor who amended it authenticates it and wrote it with the
        // signer; amended by someone who is not a doctor, no one authenticates it, as no one attests the Composition.
        var data = AbdmTestData.DischargeSummaryRecord(pdf: null);
        var amended = data with { Summary = data.Summary with { Status = DischargeSummaryStatus.Amended, AmendedAt = TestData.Now.AddHours(-1) } };

        var byDoctor = Build(AbdmRecords.DischargeSummary(amended with { AmendedByPractitionerId = TestData.DoctorId }));
        var text = byDoctor.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().Single();
        text.Authenticator.Reference.ShouldBe(FullUrl<R4.Practitioner>(byDoctor, p => p.Name[0].Text == "Dr. Asha Rao"));
        text.Author.Select(a => a.Reference).ShouldBe([
            FullUrl<R4.Practitioner>(byDoctor, p => p.Name[0].Text == "Dr. Vikram Shah"),
            FullUrl<R4.Practitioner>(byDoctor, p => p.Name[0].Text == "Dr. Asha Rao"),
        ]);

        var byClerk = Build(AbdmRecords.DischargeSummary(amended));
        var clerks = byClerk.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().Single();
        clerks.Authenticator.ShouldBeNull();
        clerks.Author.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl<R4.Practitioner>(byClerk));
    }

    [Fact]
    public void A_doctor_named_twice_in_one_resource_is_referenced_both_times()
    {
        // The signed summary's signer is its author and its authenticator, one reference object in two places: both point
        // into the document.
        var bundle = Sample("discharge-summary");
        var signer = FullUrl<R4.Practitioner>(bundle);
        var text = bundle.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().Single();

        text.Author.ShouldHaveSingleItem().Reference.ShouldBe(signer);
        text.Authenticator.Reference.ShouldBe(signer);
    }

    [Fact]
    public void A_reference_outside_the_document_without_a_type_keeps_a_display()
    {
        var data = AbdmTestData.LabReportRecord(pdf: null);
        var document = AbdmRecords.LabReport(data) with
        {
            Supporting = [new R4.Observation { Id = "orphan", Status = R4.ObservationStatus.Final, Code = new R4.CodeableConcept { Text = "Note" }, Focus = [new R4.ResourceReference("orphan")] }],
        };

        Should.NotThrow(() => Build(document));
    }

    [Fact]
    public void A_test_sent_to_a_referral_laboratory_is_performed_by_it()
    {
        // CRP went out to an accredited laboratory, as the printed report says; haemoglobin was done in-house.
        var referral = new ReferralLabRow
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-0000000ad010"),
            Code = "RL-METRO",
            Name = "Metropolis Healthcare, Pune",
            Phone = "+912066001234",
            Address = "Shivajinagar, Pune",
            AccreditationNumber = "MC-2108",
            CreatedAt = TestData.Now.AddYears(-1),
        };
        var data = AbdmTestData.LabReportRecord(pdf: null);
        var bundle = Build(AbdmRecords.LabReport(data with
        {
            Context = data.Context with { ReferralLabs = [referral] },
            Results = [data.Results[0], data.Results[1] with { ReferralLabId = referral.Id }],
        }));

        var results = bundle.Entry.Select(e => e.Resource).OfType<R4.Observation>().ToList();
        var hospital = FullUrl<R4.Organization>(bundle, o => o.Name == "Nana Hospitals, Pune");
        var laboratory = FullUrl<R4.Organization>(bundle, o => o.Name == "Metropolis Healthcare, Pune");
        results[0].Performer.ShouldHaveSingleItem().Reference.ShouldBe(hospital);
        results[1].Performer.ShouldHaveSingleItem().Reference.ShouldBe(laboratory);
        var organization = bundle.Entry.Select(e => e.Resource).OfType<R4.Organization>().Single(o => o.Name == "Metropolis Healthcare, Pune");
        organization.Identifier.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            i => i.System.ShouldBe(AbdmSystems.ReferralLab), i => i.Value.ShouldBe("RL-METRO"));

        // The hospital issues the report and verified the result.
        var report = bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().Single();
        report.Performer.ShouldHaveSingleItem().Reference.ShouldBe(hospital);
        var validation = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);
        validation.Errors.ShouldBeEmpty(validation.Describe());
    }

    [Theory]
    [InlineData("prescription")]
    [InlineData("op-consultation")]
    public void A_consultations_encounter_does_not_point_to_a_visit_outside_the_document(string name) =>
        Sample(name).Entry.Select(e => e.Resource).OfType<R4.Encounter>().Single().PartOf.ShouldBeNull();

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

    /// <summary>
    /// Each sample document validates against the NRCeS ndhm.in 6.5.0 profiles with Firely's validator, as CI runs it
    /// (<see cref="AbdmProfileValidator"/>; <c>ValidatorSetupTests</c> shows it fails documents that break the profiles).
    /// </summary>
    [Theory]
    [MemberData(nameof(RecordTypes))]
    public void Every_sample_document_validates_against_the_ig(string name)
    {
        var validation = AbdmProfileValidator.Validate(Sample(name), AbdmProfileValidator.DocumentBundle);

        validation.Errors.ShouldBeEmpty(validation.Describe());
        validation.Warnings.ShouldBeEmpty(validation.Describe());
    }

    /// <summary>
    /// The radiology sample validates because it meets DiagnosticReportImaging and ImagingStudy, not because they go
    /// unchecked: broken against them, it fails on the rule it breaks.
    /// </summary>
    [Theory]
    [InlineData("no media entry", "Missing required member: 'media'")]
    [InlineData("an empty media link", "ele-1")]
    [InlineData("an absent reason the value set lacks", "Code 'no-key-image' does not exist in the value set 'DataAbsentReason'")]
    [InlineData("no interpreter", "Missing required member: 'resultsInterpreter'")]
    [InlineData("no conclusion", "Missing required member: 'conclusion'")]
    [InlineData("the service code kept", "is not exactly equal to fixed value 'http://loinc.org'")]
    [InlineData("a series without its uid", "Missing required member: 'uid'")]
    public void A_radiology_report_broken_against_the_imaging_profiles_fails(string breakage, string expected)
    {
        var bundle = Sample("radiology-report");
        var report = bundle.Entry.Select(e => e.Resource).OfType<R4.DiagnosticReport>().Single();
        switch (breakage)
        {
            case "no media entry":
                report.Media.Clear();
                break;
            case "an empty media link":
                report.Media[0].Link = new R4.ResourceReference();
                break;
            case "an absent reason the value set lacks":
                report.Media[0].Link.Extension[0].Value = new R4.Code("no-key-image");
                break;
            case "no interpreter":
                report.ResultsInterpreter.Clear();
                break;
            case "no conclusion":
                report.Conclusion = null;
                break;
            case "the service code kept":
                report.Code.Coding.Add(new R4.Coding(FhirSystems.Service, "RAD-XR-CHEST", "X-ray chest PA view"));
                break;
            case "a series without its uid":
                bundle.Entry.Select(e => e.Resource).OfType<R4.ImagingStudy>().Single().Series.Add(new R4.ImagingStudy.SeriesComponent
                {
                    Modality = new R4.Coding(AbdmSystems.Dicom, "CR", "Computed Radiography"),
                });
                break;
        }

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        TestContext.Current.SendDiagnosticMessage($"{breakage}: {result.Describe()}");
        result.Errors.ShouldContain(e => (e.Details.Text ?? e.Diagnostics ?? string.Empty).Contains(expected), result.Describe());
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

    private static AllergyRow ChangeStatus(AllergyRow allergy, AllergyStatus status) => allergy with { Status = status };

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
