using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests.Abdm;

/// <summary>
/// The validator setup is real: the implementation guide's own discharge summary example passes with it, and a bundle
/// broken against the profiles fails, for each kind of rule the profiles make (cardinality, fixed values, slicing,
/// invariants, the profiles of referenced resources, required terminology bindings checked offline).
/// </summary>
public sealed class ValidatorSetupTests
{
    private const string Example = "Bundle-DischargeSummary-example-04.json";

    [Fact]
    public void The_igs_own_discharge_summary_example_validates()
    {
        var bundle = AbdmProfileValidator.Example(Example).ShouldBeOfType<R4.Bundle>();

        var result = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        result.Errors.ShouldBeEmpty(result.Describe());
        bundle.Entry.Count.ShouldBe(19, "the example is complete (Composition, Patient, Practitioner, reports, observations...)");
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
}
