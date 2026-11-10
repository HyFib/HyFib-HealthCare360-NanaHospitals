using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests.Abdm;

/// <summary>
/// The validator setup is real: the implementation guide's own discharge summary, prescription, immunisation record and
/// health document examples pass with it, and a bundle broken against the profiles fails, for each kind of rule the
/// profiles make (cardinality, fixed values, slicing, invariants, the profiles of referenced resources, required
/// terminology bindings checked offline).
/// </summary>
public sealed class ValidatorSetupTests
{
    private const string Example = "Bundle-DischargeSummary-example-04.json";
    private const string PrescriptionExample = "Bundle-Prescription-example-06.json";
    private const string HealthDocumentExample = "Bundle-HealthDocumentRecord-example-01.json";
    private const string WellnessExample = "Bundle-WellnessRecord-example-01.json";

    [Fact]
    public void The_igs_own_discharge_summary_example_validates()
    {
        var bundle = AbdmProfileValidator.Example(Example).ShouldBeOfType<R4.Bundle>();

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        result.Errors.ShouldBeEmpty(result.Describe());
        bundle.Entry.Count.ShouldBe(19, "the example is complete (Composition, Patient, Practitioner, reports, observations...)");
    }

    [Fact]
    public void The_igs_own_immunisation_record_example_validates()
    {
        var bundle = AbdmProfileValidator.Example("Bundle-ImmunizationRecord-example-07.json").ShouldBeOfType<R4.Bundle>();

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        result.Errors.ShouldBeEmpty(result.Describe());
        bundle.Entry.Select(e => e.Resource.TypeName).ShouldBe(
            ["Composition", "Practitioner", "Organization", "Patient", "Immunization", "ImmunizationRecommendation", "DocumentReference"],
            "the example is complete: the dose, the next dose due and a certificate");
    }

    [Theory]
    [InlineData("no bundle identifier", "bdl-9")]
    [InlineData("loinc composition type", "is not exactly equal to fixed value '373942005'")]
    [InlineData("no encounter", "Missing required member: 'encounter'")]
    [InlineData("patient without identifier", "Missing required member: 'identifier'")]
    [InlineData("composition not first", "bdl-11")]
    [InlineData("unknown section first", "open-at-end")]
    [InlineData("icd-11 condition", "does not match any slice and the group is closed")]
    [InlineData("no section", "Missing required member: 'section'")]
    [InlineData("not a mime type", "is not a valid MIME type")]
    public void A_bundle_broken_against_the_profiles_fails(string breakage, string expected)
    {
        var bundle = AbdmProfileValidator.Example(Example).ShouldBeOfType<R4.Bundle>();
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        switch (breakage)
        {
            case "no bundle identifier":
                bundle.Identifier = null;
                break;
            case "loinc composition type":
                composition.Type.Coding[0] = new R4.Coding("http://loinc.org", "18842-5", "Discharge summary");
                break;
            case "no encounter":
                composition.Encounter = null;
                break;
            case "patient without identifier":
                bundle.Entry.Select(e => e.Resource).OfType<R4.Patient>().Single().Identifier.Clear();
                break;
            case "composition not first":
                bundle.Entry.Add(bundle.Entry[0]);
                bundle.Entry.RemoveAt(0);
                break;
            case "unknown section first":
                composition.Section.Insert(0, new R4.Composition.SectionComponent
                {
                    Title = "Course in hospital",
                    Code = new R4.CodeableConcept("http://loinc.org", "8648-8", "Hospital course Narrative"),
                    Text = new R4.Narrative
                    {
                        Status = R4.Narrative.NarrativeStatus.Additional,
                        Div = "<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Recovered.</p></div>",
                    },
                });
                break;
            case "icd-11 condition":
                bundle.Entry.Select(e => e.Resource).OfType<R4.Condition>().First().Code.Coding[0] =
                    new R4.Coding("http://id.who.int/icd/release/11/mms", "BA41", "Acute myocardial infarction");
                break;
            case "no section":
                composition.Section.Clear();
                break;
            case "not a mime type":
                bundle.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().Single().Content[0].Attachment.ContentType = "PDF document";
                break;
        }

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        TestContext.Current.SendDiagnosticMessage($"{breakage}: {result.Describe()}");
        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldContain(e => (e.Details.Text ?? e.Diagnostics ?? string.Empty).Contains(expected), result.Describe());
    }

    [Fact]
    public void The_igs_own_prescription_example_validates()
    {
        var bundle = AbdmProfileValidator.Example(PrescriptionExample).ShouldBeOfType<R4.Bundle>();

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        result.Errors.ShouldBeEmpty(result.Describe());
        bundle.Entry.Select(e => e.Resource.TypeName).ShouldBe(
            ["Composition", "Patient", "Practitioner", "MedicationRequest", "MedicationRequest", "Condition", "Binary"]);
    }

    [Fact]
    public void The_igs_own_health_document_example_validates()
    {
        var bundle = AbdmProfileValidator.Example(HealthDocumentExample).ShouldBeOfType<R4.Bundle>();

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        result.Errors.ShouldBeEmpty(result.Describe());
        bundle.Entry.Select(e => e.Resource.TypeName).ShouldBe(["Composition", "Practitioner", "Patient", "DocumentReference"], "the example is complete");
    }

    /// <summary>
    /// HealthDocumentRecord's own rules are checked, not skipped: its fixed type, its one section of documents, and the
    /// document a section entry must be, with its content in the attachment.
    /// </summary>
    [Theory]
    [InlineData("loinc composition type", "is not exactly equal to fixed value '419891008'")]
    [InlineData("two sections", "cardinality of 1..1")]
    [InlineData("an empty section", "Missing required member: 'entry'")]
    [InlineData("a section entry that is not a document", "DocumentReference")]
    [InlineData("an attachment without its content", "Missing required member: 'data'")]
    [InlineData("an attachment of no content type", "Missing required member: 'contentType'")]
    [InlineData("no author", "Missing required member: 'author'")]
    public void A_health_document_broken_against_the_profiles_fails(string breakage, string expected)
    {
        var bundle = AbdmProfileValidator.Example(HealthDocumentExample).ShouldBeOfType<R4.Bundle>();
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        var attachment = bundle.Entry.Select(e => e.Resource).OfType<R4.DocumentReference>().Single().Content[0].Attachment;
        switch (breakage)
        {
            case "loinc composition type":
                composition.Type.Coding[0] = new R4.Coding("http://loinc.org", "11506-3", "Provider-unspecified Progress note");
                break;
            case "two sections":
                composition.Section.Add(new R4.Composition.SectionComponent
                {
                    Title = "Health Document",
                    Entry = [new R4.ResourceReference(composition.Section[0].Entry[0].Reference)],
                });
                break;
            case "an empty section":
                composition.Section[0].Entry.Clear();
                composition.Section[0].Text = new R4.Narrative
                {
                    Status = R4.Narrative.NarrativeStatus.Generated,
                    Div = "<div xmlns=\"http://www.w3.org/1999/xhtml\"><p>Nothing.</p></div>",
                };
                break;
            case "a section entry that is not a document":
                composition.Section[0].Entry[0] = new R4.ResourceReference(composition.Author[0].Reference) { Type = "Practitioner" };
                break;
            case "an attachment without its content":
                attachment.Data = null;
                break;
            case "an attachment of no content type":
                attachment.ContentType = null;
                break;
            case "no author":
                composition.Author.Clear();
                break;
        }

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        TestContext.Current.SendDiagnosticMessage($"{breakage}: {result.Describe()}");
        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldContain(e => (e.Details.Text ?? e.Diagnostics ?? string.Empty).Contains(expected), result.Describe());
    }

    [Fact]
    public void The_igs_own_wellness_record_example_validates()
    {
        var bundle = AbdmProfileValidator.Example(WellnessExample).ShouldBeOfType<R4.Bundle>();

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        result.Errors.ShouldBeEmpty(result.Describe());
        ((R4.Composition)bundle.Entry[0].Resource).Section.Select(s => s.Title).ShouldBe(
            ["Vital Signs", "Body Measurement", "Physical Activity", "General Assessment", "Women Health", "Lifestyle", "Document Reference"],
            "every section the guide has");
    }

    /// <summary>
    /// WellnessRecord's own rules are checked, not skipped: its type fixed as text, a patient, at least one section, and
    /// what each section's entries must be (a vital sign valued by a number or text, an entry that refers to its resource).
    /// </summary>
    [Theory]
    [InlineData("a type of other text", "is not exactly equal to fixed value 'Wellness Record'")]
    [InlineData("no patient", "Missing required member: 'subject'")]
    [InlineData("no section", "Missing required member: 'section'")]
    [InlineData("a vital sign valued as a code", "is of type 'CodeableConcept', which is not one of the allowed choice types ('Quantity','string')")]
    [InlineData("a vital sign that is not an observation", "is not one of the allowed target types ('Observation'). (for slice VitalSigns)")]
    [InlineData("a vital-signs entry naming nothing", "Missing required member: 'reference' (for slice VitalSigns)")]
    public void A_wellness_record_broken_against_the_profiles_fails(string breakage, string expected)
    {
        var bundle = AbdmProfileValidator.Example(WellnessExample).ShouldBeOfType<R4.Bundle>();
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        var vitals = composition.Section.Single(s => s.Title == "Vital Signs");
        switch (breakage)
        {
            case "a type of other text":
                composition.Type.Text = "Wellness record";
                break;
            case "no patient":
                composition.Subject = null;
                break;
            case "no section":
                composition.Section.Clear();
                break;
            case "a vital sign valued as a code":
                var heartRate = (R4.Observation)bundle.Entry.Single(e => e.FullUrl == vitals.Entry[1].Reference).Resource;
                heartRate.Value = new R4.CodeableConcept { Text = "Atrial fibrillation suspected" };
                break;
            case "a vital sign that is not an observation":
                vitals.Entry[0] = new R4.ResourceReference(composition.Subject.Reference) { Type = "Patient" };
                break;
            case "a vital-signs entry naming nothing":
                vitals.Entry[0] = new R4.ResourceReference { Display = "Respiratory rate" };
                break;
        }

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        TestContext.Current.SendDiagnosticMessage($"{breakage}: {result.Describe()}");
        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldContain(e => (e.Details.Text ?? e.Diagnostics ?? string.Empty).Contains(expected), result.Describe());
    }

    [Theory]
    [InlineData("untyped section entry", "Missing required member: 'type'")]
    [InlineData("two sections", "cardinality of 1..1")]
    [InlineData("medicine coded outside snomed", "is not exactly equal to fixed value 'http://snomed.info/sct'")]
    [InlineData("no dosage", "Missing required member: 'dosageInstruction'")]
    [InlineData("no requester", "Missing required member: 'requester'")]
    public void A_prescription_broken_against_the_profiles_fails(string breakage, string expected)
    {
        var bundle = AbdmProfileValidator.Example(PrescriptionExample).ShouldBeOfType<R4.Bundle>();
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        var request = bundle.Entry.Select(e => e.Resource).OfType<R4.MedicationRequest>().First();
        switch (breakage)
        {
            case "untyped section entry":
                composition.Section[0].Entry[0].Type = null;
                break;
            case "two sections":
                composition.Section.Add(new R4.Composition.SectionComponent
                {
                    Title = "Prescription record",
                    Code = new R4.CodeableConcept("http://snomed.info/sct", "440545006", "Prescription record"),
                    Entry = [new R4.ResourceReference(composition.Section[0].Entry[0].Reference) { Type = "MedicationRequest" }],
                });
                break;
            case "medicine coded outside snomed":
                request.Medication = new R4.CodeableConcept("urn:hc360:drug", "AZI-250", "Azithromycin 250 mg tablet");
                break;
            case "no dosage":
                request.DosageInstruction.Clear();
                break;
            case "no requester":
                request.Requester = null;
                break;
        }

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        TestContext.Current.SendDiagnosticMessage($"{breakage}: {result.Describe()}");
        result.Errors.ShouldNotBeEmpty();
        result.Errors.ShouldContain(e => (e.Details.Text ?? e.Diagnostics ?? string.Empty).Contains(expected), result.Describe());
    }
}
