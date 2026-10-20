using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests.Abdm;

/// <summary>
/// A signed discharge summary as an ABDM health record: the document validates against the NRCeS ndhm.in 6.5.0 profiles
/// (DocumentBundle, DischargeSummaryRecord and the profiles of every resource in it), and maps what HC360 holds, no more.
/// </summary>
public sealed class DischargeSummaryRecordTests
{
    private static R4.Bundle Map(DischargeSummarySource source) =>
        DischargeSummaryRecordMapper.Map(source, AbdmTestData.DocumentId, TestData.Now);

    private static T Single<T>(R4.Bundle bundle) where T : R4.Resource => bundle.Entry.Select(e => e.Resource).OfType<T>().ShouldHaveSingleItem();

    private static T Resolve<T>(R4.Bundle bundle, R4.ResourceReference reference) where T : R4.Resource =>
        bundle.Entry.Where(e => e.FullUrl == reference.Reference).ShouldHaveSingleItem().Resource.ShouldBeOfType<T>();

    /// <summary>No error, and no warning either: every rule of the profiles holds, and no terminology check was skipped.</summary>
    private static void ShouldValidate(R4.Bundle bundle)
    {
        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        result.Errors.ShouldBeEmpty(result.Describe());
        result.Warnings.ShouldBeEmpty(result.Describe());
        result.UncheckedValueSets.ShouldBeEmpty();
    }

    [Fact]
    public void A_signed_summary_validates_against_the_ndhm_profiles() => ShouldValidate(Map(AbdmTestData.Source()));

    [Fact]
    public void The_least_a_signed_summary_holds_validates_too()
    {
        // No ICD codes, ABHA number, HFR id, doctor, admission record, advice or follow-up.
        var bundle = Map(AbdmTestData.Minimal());

        ShouldValidate(bundle);
        bundle.Entry.Select(e => e.Resource.TypeName).ShouldBe(["Composition", "Patient", "Encounter", "Organization", "DocumentReference"]);
    }

    [Fact]
    public void An_amended_summary_validates_and_says_it_was_amended()
    {
        var source = AbdmTestData.Source() with { Summary = AbdmTestData.Summary(DischargeSummaryStatus.Amended) };

        var bundle = Map(source);

        ShouldValidate(bundle);
        var composition = Single<R4.Composition>(bundle);
        composition.Status.ShouldBe(R4.CompositionStatus.Amended);
        composition.Date.ShouldBe("2026-09-25T10:30:00+05:30");
        Single<R4.DocumentReference>(bundle).DocStatus.ShouldBe(R4.CompositionStatus.Amended);
    }

    [Fact]
    public void Text_with_markup_and_control_characters_is_carried_as_text()
    {
        var summary = AbdmTestData.Summary() with
        {
            CourseInHospital = "Improved <quickly> & steadily\u0001\nSpO2 > 95% \"on air\"",
            Advice = "Avoid <script>alert(1)</script>",
        };

        var bundle = Map(AbdmTestData.Source() with { Summary = summary });

        ShouldValidate(bundle);
        var course = Single<R4.Composition>(bundle).Section.Single(s => s.Title == "Course in hospital");
        course.Text.Div.ShouldBe(
            "<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Improved &lt;quickly&gt; &amp; steadily<br/>SpO2 &gt; 95% &quot;on air&quot;</p></div>");
        Single<R4.Composition>(bundle).Section.Single(s => s.Title == "Advice and follow-up").Text.Div
            .ShouldContain("Avoid &lt;script&gt;alert(1)&lt;/script&gt;");
    }

    [Fact]
    public void The_document_is_a_bundle_of_type_document_led_by_its_composition()
    {
        var bundle = Map(AbdmTestData.Source());

        bundle.Type.ShouldBe(R4.Bundle.BundleType.Document);
        bundle.Meta.Profile.ShouldBe([AbdmProfiles.DocumentBundle]);
        bundle.Meta.VersionId.ShouldBe("1");
        bundle.Identifier.System.ShouldBe("urn:ietf:rfc:3986");
        bundle.Identifier.Value.ShouldBe($"urn:uuid:{AbdmTestData.DocumentId:D}");
        bundle.Timestamp.ShouldBe(TestData.Now);
        bundle.Entry[0].Resource.ShouldBeOfType<R4.Composition>();
        bundle.Entry.Select(e => e.Resource.TypeName).ShouldBe(
            ["Composition", "Patient", "Encounter", "Practitioner", "Organization", "Condition", "Condition", "Condition", "CarePlan", "DocumentReference"]);
        bundle.Entry.ShouldAllBe(e => e.FullUrl == "urn:uuid:" + e.Resource.Id && e.Resource.Meta.Profile.Count() == 1);
    }

    [Fact]
    public void Every_reference_points_at_an_entry_or_names_its_target()
    {
        var bundle = Map(AbdmTestData.Source());
        var fullUrls = bundle.Entry.Select(e => e.FullUrl).ToHashSet();

        var references = bundle.Entry.SelectMany(e => DocumentBundleBuilder.References(e.Resource)).ToList();

        references.Count.ShouldBeGreaterThan(20);
        references.Where(r => r.Reference is not null).ShouldAllBe(r => fullUrls.Contains(r.Reference));
        references.Where(r => r.Reference is null).ShouldAllBe(r => r.Display != null || r.Identifier != null);
    }

    [Fact]
    public void The_composition_is_a_discharge_summary_signed_by_the_doctor_and_kept_by_the_hospital()
    {
        var bundle = Map(AbdmTestData.Source());
        var composition = Single<R4.Composition>(bundle);

        composition.Id.ShouldBe(AbdmTestData.SummaryId.ToString("D"));
        composition.Meta.Profile.ShouldBe([AbdmProfiles.DischargeSummaryRecord]);
        composition.Identifier.Value.ShouldBe($"urn:uuid:{AbdmTestData.SummaryId:D}");
        composition.Status.ShouldBe(R4.CompositionStatus.Final);
        var type = composition.Type.Coding.ShouldHaveSingleItem();
        (type.System, type.Code, type.Display).ShouldBe(("http://snomed.info/sct", "373942005", "Discharge summary"));
        composition.Date.ShouldBe("2026-09-25T09:30:00+05:30");
        Resolve<R4.Patient>(bundle, composition.Subject).Id.ShouldBe(TestData.PatientId.ToString("D"));
        Resolve<R4.Encounter>(bundle, composition.Encounter).Id.ShouldBe(AbdmTestData.VisitId.ToString("D"));
        Resolve<R4.Practitioner>(bundle, composition.Author.ShouldHaveSingleItem()).Id.ShouldBe(TestData.DoctorId.ToString("D"));
        var attester = composition.Attester.ShouldHaveSingleItem();
        attester.Mode.ShouldBe(R4.Composition.CompositionAttestationMode.Legal);
        attester.Time.ShouldBe("2026-09-25T09:30:00+05:30");
        Resolve<R4.Practitioner>(bundle, attester.Party);
        Resolve<R4.Organization>(bundle, composition.Custodian).Name.ShouldBe("Nana Hospitals, Pune");
    }

    [Fact]
    public void The_profiles_sections_come_first_and_the_others_after_them()
    {
        var sections = Single<R4.Composition>(Map(AbdmTestData.Source())).Section;

        sections.Select(s => (s.Title, s.Code.Coding.ShouldHaveSingleItem().Code)).ShouldBe(
        [
            ("Presenting complaints", "422843007"),
            ("Condition at discharge", "425044008"),
            ("Investigations", "721981007"),
            ("Medicines on discharge", "1003606003"),
            ("Procedures and surgeries", "1003640003"),
            ("Advice and follow-up", "734163000"),
            ("Discharge summary (full text)", "373942005"),
            ("Final diagnosis", "11535-2"),
            ("Course in hospital", "8648-8"),
        ]);
        sections.Take(7).ShouldAllBe(s => s.Code.Coding[0].System == "http://snomed.info/sct");
        sections.Skip(7).ShouldAllBe(s => s.Code.Coding[0].System == "http://loinc.org");
    }

    [Fact]
    public void Each_section_carries_what_the_doctor_wrote_as_written()
    {
        var sections = Single<R4.Composition>(Map(AbdmTestData.Source())).Section;

        string Div(string title) => sections.Single(s => s.Title == title).Text.Div.ShouldNotBeNull();
        sections.Where(s => s.Text != null).ShouldAllBe(s => s.Text.Status == R4.Narrative.NarrativeStatus.Additional);
        Div("Presenting complaints").ShouldBe("<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Fever and cough with breathlessness for 4 days</p></div>");
        Div("Investigations").ShouldContain("<p>Chest X-ray: right lower lobe consolidation<br/>CBC: TLC 14,200/µL</p>");
        Div("Course in hospital").ShouldBe(
            "<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Treated with IV antibiotics and oxygen.</p><p>Afebrile from day 3; oxygen stopped on day 3.</p></div>");
        Div("Medicines on discharge").ShouldContain("Tab. Amoxicillin-clavulanate 625 mg BD x 5 days<br/>Tab. Metformin 500 mg BD");
        Div("Condition at discharge").ShouldContain("Stable, afebrile, SpO2 97% on room air");
        Div("Advice and follow-up").ShouldContain("<b>Follow-up:</b> 02 Oct 2026: Review in the medicine OPD with a repeat chest X-ray");
        Div("Final diagnosis").ShouldContain("<p>Community-acquired pneumonia, right lower lobe<br/>Type 2 diabetes mellitus</p>");
        Div("Final diagnosis").ShouldContain("<li>J18.9 Pneumonia, unspecified (ICD-10)</li>");
        Div("Final diagnosis").ShouldContain("<li>1A00 Cholera (ICD-11)</li>");
        sections.Single(s => s.Title == "Discharge summary (full text)").Text.ShouldBeNull();
    }

    [Fact]
    public void Parts_of_the_summary_left_empty_have_no_section()
    {
        var titles = Single<R4.Composition>(Map(AbdmTestData.Minimal())).Section.Select(s => s.Title);

        titles.ShouldBe(["Condition at discharge", "Discharge summary (full text)", "Final diagnosis", "Course in hospital"]);
    }

    [Fact]
    public void Each_icd_10_code_is_a_coded_diagnosis_of_the_stay()
    {
        var bundle = Map(AbdmTestData.Source());
        var conditions = bundle.Entry.Select(e => e.Resource).OfType<R4.Condition>().ToList();

        conditions.Count.ShouldBe(3);
        var pneumonia = conditions[0];
        var coding = pneumonia.Code.Coding.ShouldHaveSingleItem();
        (coding.System, coding.Code, coding.Display).ShouldBe(("http://hl7.org/fhir/sid/icd-10", "J18.9", "Pneumonia, unspecified"));
        pneumonia.Code.Text.ShouldBe("Pneumonia, unspecified");
        pneumonia.VerificationStatus.Coding[0].Code.ShouldBe("confirmed");
        pneumonia.Category.ShouldHaveSingleItem().Coding[0].Code.ShouldBe("encounter-diagnosis");
        pneumonia.ClinicalStatus.Coding[0].Code.ShouldBe("active");
        pneumonia.Onset.ShouldBeOfType<R4.FhirDateTime>().Value.ShouldBe("2026-09-20");
        pneumonia.RecordedDate.ShouldBe("2026-09-25T09:30:00+05:30");
        Resolve<R4.Practitioner>(bundle, pneumonia.Recorder);
        Resolve<R4.Encounter>(bundle, pneumonia.Encounter);
        Resolve<R4.Patient>(bundle, pneumonia.Subject);

        var diabetes = conditions[1];
        diabetes.ClinicalStatus.Coding[0].Code.ShouldBe("active");
        diabetes.ClinicalStatus.Text.ShouldBe("Chronic");
        diabetes.Onset.ShouldBeOfType<R4.FhirDateTime>().Value.ShouldBe("2018-01-01");

        // The NRCeS Condition admits ICD-10 and SNOMED CT codings only: an ICD-11 code is kept in the text. Nobody
        // recorded it during the stay, so it is active as of the summary, which names it as a final diagnosis.
        var cholera = conditions[2];
        cholera.Code.Coding.ShouldBeEmpty();
        cholera.Code.Text.ShouldBe("Cholera (ICD-11 1A00)");
        cholera.ClinicalStatus.Coding[0].Code.ShouldBe("active");
        cholera.Onset.ShouldBeNull();

        Single<R4.Composition>(bundle).Section.Single(s => s.Title == "Final diagnosis").Entry
            .Select(e => Resolve<R4.Condition>(bundle, e)).ShouldBe(conditions);
        Single<R4.Encounter>(bundle).Diagnosis.ShouldBeEmpty("the conditions name the stay; it does not list them back");
    }

    [Fact]
    public void A_diagnosis_resolved_during_the_stay_is_resolved_on_its_date()
    {
        var source = AbdmTestData.Source() with
        {
            RecordedDiagnoses = new Dictionary<string, RecordedDiagnosis>
            {
                ["J18.9"] = new(DiagnosisStatus.Resolved, new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 24)),
            },
        };

        var bundle = Map(source);

        ShouldValidate(bundle);
        var pneumonia = bundle.Entry.Select(e => e.Resource).OfType<R4.Condition>().First();
        pneumonia.ClinicalStatus.Coding[0].Code.ShouldBe("resolved");
        pneumonia.Abatement.ShouldBeOfType<R4.FhirDateTime>().Value.ShouldBe("2026-09-24");
    }

    [Fact]
    public void A_code_whose_title_is_unknown_is_written_in_the_text_only()
    {
        var source = AbdmTestData.Source() with { CodeTitles = new Dictionary<string, string>() };

        var conditions = Map(source).Entry.Select(e => e.Resource).OfType<R4.Condition>().ToList();

        conditions.ShouldAllBe(c => c.Code.Coding.Count == 0);
        conditions[0].Code.Text.ShouldBe("J18.9 (ICD-10 J18.9)");
    }

    [Fact]
    public void Derived_resources_keep_their_ids_from_one_export_to_the_next()
    {
        var first = Map(AbdmTestData.Source());
        var second = DischargeSummaryRecordMapper.Map(AbdmTestData.Source(), Guid.NewGuid(), TestData.Now.AddDays(1));

        second.Entry.Select(e => e.FullUrl).ShouldBe(first.Entry.Select(e => e.FullUrl));
        second.Identifier.Value.ShouldNotBe(first.Identifier.Value, "each export is a new document");
    }

    [Fact]
    public void The_patient_carries_the_uhid_and_the_abha_number()
    {
        var bundle = Map(AbdmTestData.Source());
        var patient = Single<R4.Patient>(bundle);

        patient.Meta.Profile.ShouldBe([AbdmProfiles.Patient]);
        patient.Identifier.Select(i => (i.System, i.Value)).ShouldBe(
            [("urn:hc360:uhid", "NH-UH-2600123"), ("https://healthid.ndhm.gov.in", "91-2345-6789-0123")]);
        patient.Identifier.ShouldAllBe(i => i.Type.Coding.Count == 1);
        patient.Name.ShouldHaveSingleItem().Text.ShouldBe("Meera Anil Kulkarni");
        Resolve<R4.Organization>(bundle, patient.ManagingOrganization);

        Single<R4.Patient>(Map(AbdmTestData.Minimal())).Identifier.ShouldHaveSingleItem().System.ShouldBe("urn:hc360:uhid");
    }

    [Fact]
    public void The_doctor_is_named_by_council_registration_and_hc360_code_without_contact_details()
    {
        var practitioner = Single<R4.Practitioner>(Map(AbdmTestData.Source()));

        var registration = practitioner.Identifier[0];
        registration.Type.Coding[0].Code.ShouldBe("MD");
        registration.Value.ShouldBe("2009/03/1234");
        registration.System.ShouldBeNull();
        registration.Assigner.Display.ShouldBe("Maharashtra Medical Council");
        var code = practitioner.Identifier[1];
        (code.System, code.Value, code.Type.Coding[0].Code).ShouldBe(("urn:hc360:practitioner", "DR-ASHA", "PRN"));
        var name = practitioner.Name.ShouldHaveSingleItem();
        (name.Text, name.Family, name.Given.Single(), name.Prefix.Single()).ShouldBe(("Dr. Asha Rao", "Rao", "Asha", "Dr."));
        practitioner.Gender.ShouldBe(R4.AdministrativeGender.Female);
        practitioner.Qualification.ShouldHaveSingleItem().Code.Text.ShouldBe("MBBS, MD (General Medicine)");
        practitioner.Telecom.ShouldBeEmpty();
    }

    [Fact]
    public void The_hospital_is_named_by_its_hfr_id_and_hc360_code()
    {
        var organization = Single<R4.Organization>(Map(AbdmTestData.Source()));

        organization.Identifier.Select(i => (i.System, i.Value, i.Type.Coding[0].Code)).ShouldBe(
            [("https://facility.ndhm.gov.in", "IN2710000123", "PRN"), ("urn:hc360:facility", "NHP", "XX")]);
        organization.Type.ShouldHaveSingleItem().Coding[0].Code.ShouldBe("prov");
        organization.Telecom.Select(t => t.Use).ShouldAllBe(u => u == R4.ContactPoint.ContactPointUse.Work);
        organization.Address.ShouldHaveSingleItem().City.ShouldBe("Pune");

        Single<R4.Organization>(Map(AbdmTestData.Minimal())).Identifier.ShouldHaveSingleItem().System.ShouldBe("urn:hc360:facility");
    }

    [Fact]
    public void The_stay_is_the_in_patient_encounter_with_its_admission()
    {
        var bundle = Map(AbdmTestData.Source());
        var encounter = Single<R4.Encounter>(bundle);

        encounter.Class.Code.ShouldBe("IMP");
        encounter.Status.ShouldBe(R4.Encounter.EncounterStatus.Finished);
        encounter.Identifier.Select(i => i.Value).ShouldBe(["NHIP260000042", "NHADM260000007"]);
        encounter.Period.Start.ShouldBe("2026-09-21T10:30:00+05:30");
        encounter.Hospitalization.DischargeDisposition.Coding[0].Code.ShouldBe("home");
        Resolve<R4.Patient>(bundle, encounter.Subject);
        Resolve<R4.Organization>(bundle, encounter.ServiceProvider);
        encounter.Participant.ShouldNotBeEmpty();
        encounter.Participant.ShouldAllBe(p => p.Individual.Reference == $"urn:uuid:{TestData.DoctorId:D}");
    }

    [Fact]
    public void Without_its_admission_record_the_stay_comes_from_the_summary()
    {
        var encounter = Single<R4.Encounter>(Map(AbdmTestData.Minimal() with { Summary = AbdmTestData.Summary() }));

        encounter.Id.ShouldBe(AbdmTestData.AdmissionId.ToString("D"));
        encounter.Class.Code.ShouldBe("IMP");
        encounter.Status.ShouldBe(R4.Encounter.EncounterStatus.Unknown);
        encounter.Period.End.ShouldBe("2026-09-25T10:30:00+05:30");
    }

    [Fact]
    public void Advice_and_the_follow_up_visit_are_the_care_plan()
    {
        var bundle = Map(AbdmTestData.Source());
        var plan = Single<R4.CarePlan>(bundle);

        plan.Status.ShouldBe(R4.RequestStatus.Active);
        plan.Intent.ShouldBe(R4.CarePlan.CarePlanIntent.Plan);
        plan.Description.ShouldStartWith("Plenty of fluids.");
        var visit = plan.Activity.ShouldHaveSingleItem().Detail;
        visit.Kind.ShouldBe(R4.CarePlan.CarePlanActivityKind.Appointment);
        visit.Status.ShouldBe(R4.CarePlan.CarePlanActivityStatus.NotStarted);
        visit.Scheduled.ShouldBeOfType<R4.Timing>().Event.ShouldBe(["2026-10-02"]);
        visit.Description.ShouldBe("Review in the medicine OPD with a repeat chest X-ray");
        Resolve<R4.Practitioner>(bundle, plan.Author);
    }

    [Fact]
    public void The_whole_summary_travels_as_a_text_document()
    {
        var bundle = Map(AbdmTestData.Source());
        var document = Single<R4.DocumentReference>(bundle);

        document.Type.Coding.Select(c => c.Code).ShouldBe(["18842-5", "373942005"]);
        Resolve<R4.Encounter>(bundle, document.Context.Encounter.ShouldHaveSingleItem());
        Resolve<R4.Organization>(bundle, document.Custodian);
        Resolve<R4.Practitioner>(bundle, document.Author.ShouldHaveSingleItem());
        var attachment = document.Content.ShouldHaveSingleItem().Attachment;
        attachment.ContentType.ShouldBe("text/plain; charset=utf-8");
        Encoding.UTF8.GetString(attachment.Data).ShouldContain("Course in hospital: Treated with IV antibiotics and oxygen.");
    }

    [Fact]
    public void The_file_is_named_after_the_uhid_and_the_discharge_date()
    {
        AbdmDocumentService.FileName(AbdmTestData.Summary(), "NH-UH-2600123").ShouldBe("discharge-summary-NH-UH-2600123-2026-09-25-abdm.json");
        AbdmDocumentService.FileName(AbdmTestData.Summary() with { DischargedAt = null, SignedAt = null, AdmittedAt = null }, "NH-1")
            .ShouldBe("discharge-summary-NH-1-abdm.json");
    }
}
