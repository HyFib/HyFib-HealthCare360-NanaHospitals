using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests.Abdm;

/// <summary>
/// A signed prescription as an ABDM health record: the document validates against the NRCeS ndhm.in 6.5.0 profiles
/// (DocumentBundle, PrescriptionRecord and the profiles of every resource in it), and carries each medicine line as
/// HC360 wrote it, no more.
/// </summary>
public sealed class PrescriptionRecordTests
{
    private static R4.Bundle Map(PrescriptionSource source) =>
        PrescriptionRecordMapper.Map(source, AbdmTestData.DocumentId, TestData.Now);

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
    public void A_signed_prescription_validates_against_the_ndhm_profiles() => ShouldValidate(Map(AbdmTestData.PrescriptionSource()));

    [Fact]
    public void The_least_a_signed_prescription_holds_validates_too()
    {
        var bundle = Map(AbdmTestData.MinimalPrescription());

        ShouldValidate(bundle);
        bundle.Entry.Select(e => e.Resource.TypeName).ShouldBe(["Composition", "Patient", "Organization", "MedicationRequest"]);
    }

    [Fact]
    public void An_in_patient_prescription_validates_with_the_stay_as_its_encounter()
    {
        var source = AbdmTestData.PrescriptionSource() with
        {
            Prescription = AbdmTestData.Prescription() with { VisitId = AbdmTestData.VisitId, AdmissionId = AbdmTestData.AdmissionId, IsInpatient = true },
            Lines = [.. AbdmTestData.Lines().Select(l => l with { VisitId = AbdmTestData.VisitId, AdmissionId = AbdmTestData.AdmissionId, IsInpatient = true })],
            Visit = AbdmTestData.Stay(),
        };

        var bundle = Map(source);

        ShouldValidate(bundle);
        var encounter = Single<R4.Encounter>(bundle);
        encounter.Class.Code.ShouldBe("IMP");
        encounter.Identifier.ShouldContain(i => i.Value == "NHADM260000007");
        bundle.Entry.Select(e => e.Resource).OfType<R4.MedicationRequest>()
            .ShouldAllBe(r => r.Category[0].Coding[0].Code == "inpatient" && r.Encounter.Reference == $"urn:uuid:{AbdmTestData.VisitId:D}");
    }

    [Fact]
    public void Text_with_markup_and_control_characters_is_carried_as_text()
    {
        var source = AbdmTestData.PrescriptionSource() with
        {
            Prescription = AbdmTestData.Prescription() with { Advice = "Avoid <script>alert(1)</script> & cold drinks\u0001" },
            Lines = [AbdmTestData.Line(1, "Dolo <650>", "Paracetamol & caffeine", null, "1 tab", DoseFrequency.BD, 3, 6m, "After food \"only\"")],
        };

        var bundle = Map(source);

        ShouldValidate(bundle);
        var text = Single<R4.Composition>(bundle).Section.ShouldHaveSingleItem().Text.Div;
        text.ShouldContain("<li>Dolo &lt;650&gt; Tablet (Paracetamol &amp; caffeine): 1 tab, twice a day, for 3 days. After food &quot;only&quot;</li>");
        text.ShouldContain("<p><b>Advice:</b> Avoid &lt;script&gt;alert(1)&lt;/script&gt; &amp; cold drinks</p>");
    }

    [Fact]
    public void The_document_is_a_prescription_record_led_by_its_composition()
    {
        var bundle = Map(AbdmTestData.PrescriptionSource());

        bundle.Type.ShouldBe(R4.Bundle.BundleType.Document);
        bundle.Meta.Profile.ShouldBe([AbdmProfiles.DocumentBundle]);
        bundle.Entry.Select(e => e.Resource.TypeName).ShouldBe(
            ["Composition", "Patient", "Encounter", "Practitioner", "Organization", .. Enumerable.Repeat("MedicationRequest", 7)]);
        bundle.Entry[0].Resource.Meta.Profile.ShouldBe([AbdmProfiles.PrescriptionRecord]);
        bundle.Entry.Select(e => e.Resource).OfType<R4.MedicationRequest>().ShouldAllBe(r => r.Meta.Profile.Single() == AbdmProfiles.MedicationRequest);
        bundle.Entry.ShouldAllBe(e => e.FullUrl == $"urn:uuid:{e.Resource.Id}");
    }

    [Fact]
    public void The_composition_is_the_prescription_signed_by_the_doctor_and_kept_by_the_hospital()
    {
        var bundle = Map(AbdmTestData.PrescriptionSource());
        var composition = Single<R4.Composition>(bundle);

        composition.Id.ShouldBe(AbdmTestData.PrescriptionId.ToString("D"));
        composition.Identifier.Value.ShouldBe($"urn:uuid:{AbdmTestData.PrescriptionId:D}");
        composition.Status.ShouldBe(R4.CompositionStatus.Final);
        var type = composition.Type.Coding.ShouldHaveSingleItem();
        (type.System, type.Code, type.Display).ShouldBe(("http://snomed.info/sct", "440545006", "Prescription record"));
        composition.Title.ShouldBe("Prescription NHRX260000123");
        composition.Date.ShouldBe("2026-09-25T09:30:00+05:30");
        Resolve<R4.Patient>(bundle, composition.Subject);
        Resolve<R4.Encounter>(bundle, composition.Encounter).Identifier.ShouldContain(i => i.Value == "NHOP260001234");
        Resolve<R4.Organization>(bundle, composition.Custodian).Name.ShouldBe("Nana Hospitals, Pune");
        Resolve<R4.Practitioner>(bundle, composition.Author.ShouldHaveSingleItem()).Id.ShouldBe(TestData.DoctorId.ToString("D"));
        var attester = composition.Attester.ShouldHaveSingleItem();
        attester.Mode.ShouldBe(R4.Composition.CompositionAttestationMode.Legal);
        attester.Time.ShouldBe("2026-09-25T09:30:00+05:30");
        Resolve<R4.Practitioner>(bundle, attester.Party);
    }

    [Fact]
    public void A_refill_is_the_prescribers_but_not_signed_by_them()
    {
        var source = AbdmTestData.PrescriptionSource() with
        {
            Prescription = AbdmTestData.Prescription() with { RefillOfPrescriptionId = Guid.Parse("01923a4b-0000-7000-8000-00000000e599") },
        };

        var bundle = Map(source);

        ShouldValidate(bundle);
        var composition = Single<R4.Composition>(bundle);
        Resolve<R4.Practitioner>(bundle, composition.Author.ShouldHaveSingleItem());
        composition.Attester.ShouldBeEmpty("whoever issued the refill signed it, not necessarily the prescriber");
    }

    [Fact]
    public void Its_one_section_lists_every_medicine_and_the_advice()
    {
        var bundle = Map(AbdmTestData.PrescriptionSource());
        var section = Single<R4.Composition>(bundle).Section.ShouldHaveSingleItem();

        var code = section.Code.Coding.ShouldHaveSingleItem();
        (code.System, code.Code, code.Display).ShouldBe(("http://snomed.info/sct", "440545006", "Prescription record"));
        section.Entry.Select(e => e.Type).ShouldAllBe(t => t == "MedicationRequest");
        section.Entry.Select(e => Resolve<R4.MedicationRequest>(bundle, e).Id)
            .ShouldBe([.. AbdmTestData.Lines().Select(l => l.Id.ToString("D"))], "in the order the doctor wrote them");
        section.Text.Div.ShouldBe(
            "<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Medicines:</p><ul>"
            + "<li>Dolo 650 Tablet (Paracetamol): 1 tab, three times a day, for 3 days. After food</li>"
            + "<li>Azithral 500 mg Tablet (Azithromycin): 1 tab, once a day, for 3 days. One hour before food</li>"
            + "<li>Cetirizine 10 mg Tablet: 1 tab, at bedtime, for 5 days</li>"
            + "<li>Ondansetron 4 mg Tablet: 1 tab, only when required, for 2 days. If vomiting</li>"
            + "<li>Methylprednisolone 16 mg Tablet: 1 tab, immediately, once</li>"
            + "<li>Vitamin D3 60000 IU Tablet (Cholecalciferol): 1 sachet, once a week, for 56 days</li>"
            + "<li>Pantoprazole 40 mg Tablet: 1 tab, 1-0-1, for 5 days</li></ul>"
            + "<p><b>Advice:</b> Drink plenty of fluids.<br/>Come back if the fever lasts beyond 3 days.</p></div>");
    }

    [Fact]
    public void Each_medicine_is_a_medication_request_of_the_documents_patient_visit_and_doctor()
    {
        var bundle = Map(AbdmTestData.PrescriptionSource());
        var requests = bundle.Entry.Select(e => e.Resource).OfType<R4.MedicationRequest>().ToList();

        requests.Count.ShouldBe(7);
        foreach (var request in requests)
        {
            Resolve<R4.Patient>(bundle, request.Subject);
            Resolve<R4.Encounter>(bundle, request.Encounter);
            Resolve<R4.Practitioner>(bundle, request.Requester);
            request.GroupIdentifier.Value.ShouldBe("NHRX260000123");
            request.Intent.ShouldBe(R4.MedicationRequest.MedicationRequestIntent.Order);
            request.Status.ShouldBe(R4.MedicationRequest.MedicationrequestStatus.Active);
            request.AuthoredOn.ShouldBe("2026-09-25T09:30:00+05:30");
        }

        var paracetamol = requests[0];
        var medicine = paracetamol.Medication.ShouldBeOfType<R4.CodeableConcept>();
        medicine.Text.ShouldBe("Dolo 650 Tablet (Paracetamol)");
        medicine.Coding.ShouldBeEmpty("HC360 holds no SNOMED CT medicine codes, so none is made up");
        var dosage = paracetamol.DosageInstruction.ShouldHaveSingleItem();
        dosage.Text.ShouldBe("1 tab, three times a day, for 3 days. After food");
        dosage.Timing.Code.Coding.ShouldHaveSingleItem().Code.ShouldBe("TID");
        dosage.Route.Text.ShouldBe("Oral");
        dosage.Route.Coding.ShouldBeEmpty();
        paracetamol.DispenseRequest.Quantity.Value.ShouldBe(9m);
        requests[3].DosageInstruction[0].AsNeeded.ShouldBeOfType<R4.FhirBoolean>().Value.ShouldBe(true);
        requests[4].DosageInstruction[0].Timing.Repeat.Count.ShouldBe(1);
    }

    [Fact]
    public void Without_the_doctor_on_file_the_hospital_is_named_instead()
    {
        var bundle = Map(AbdmTestData.MinimalPrescription());
        var composition = Single<R4.Composition>(bundle);

        Resolve<R4.Organization>(bundle, composition.Author.ShouldHaveSingleItem());
        composition.Attester.ShouldBeEmpty();
        composition.Encounter.ShouldBeNull();
        var request = Single<R4.MedicationRequest>(bundle);
        Resolve<R4.Organization>(bundle, request.Requester);
        request.Encounter.ShouldBeNull();
        Single<R4.Composition>(bundle).Section[0].Text.Div.ShouldNotContain("Advice");
    }

    [Fact]
    public void The_file_is_named_after_the_prescription_number_as_its_pdf_is() =>
        AbdmDocumentService.FileName(AbdmTestData.Prescription()).ShouldBe("prescription-NHRX260000123-abdm.json");
}
