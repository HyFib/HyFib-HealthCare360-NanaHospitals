using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class SearchRequestParserTests
{
    private static readonly string PatientRef = $"Patient/{TestData.PatientId:D}";

    private static SearchParse Parse(string type, string query) => SearchRequestParser.Parse(SearchDefinitions.Find(type)!, TestData.Query(query));

    private static FhirError Error(string type, string query)
    {
        var parsed = Parse(type, query);
        parsed.Request.ShouldBeNull();
        return parsed.Error.ShouldNotBeNull();
    }

    [Fact]
    public void Defaults_to_twenty_results_per_page() => TestData.Search("Observation", $"patient={PatientRef}").Count.ShouldBe(20);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("100", 100)]
    [InlineData("101", 100)]
    [InlineData("100000", 100)]
    public void Caps_count_at_one_hundred(string requested, int expected) =>
        TestData.Search("Observation", $"patient={PatientRef}&_count={requested}").Count.ShouldBe(expected);

    [Theory]
    [InlineData("-1")]
    [InlineData("ten")]
    [InlineData("1.5")]
    public void Rejects_a_malformed_count(string count) =>
        Error("Observation", $"patient={PatientRef}&_count={count}").StatusCode.ShouldBe(400);

    [Fact]
    public void Rejects_a_repeated_count() => Error("Observation", $"patient={PatientRef}&_count=5&_count=6").StatusCode.ShouldBe(400);

    [Fact]
    public void Reads_the_offset()
    {
        TestData.Search("Observation", $"patient={PatientRef}&_offset=40").Offset.ShouldBe(40);
        Error("Observation", $"patient={PatientRef}&_offset=-5").StatusCode.ShouldBe(400);
    }

    [Theory]
    [InlineData("Observation")]
    [InlineData("Encounter")]
    [InlineData("Condition")]
    [InlineData("AllergyIntolerance")]
    [InlineData("MedicationRequest")]
    [InlineData("DiagnosticReport")]
    [InlineData("DocumentReference")]
    public void Clinical_searches_need_a_patient(string type)
    {
        var error = Error(type, "_count=10");

        error.StatusCode.ShouldBe(400);
        error.Reason.ShouldBe(FhirOutcomes.PatientRequired);
    }

    [Theory]
    [InlineData("patient=Patient/{0}")]
    [InlineData("patient={0}")]
    [InlineData("subject=Patient/{0}")]
    [InlineData("patient=https://hc360.example/fhir/r4/Patient/{0}")]
    public void Accepts_the_patient_reference_forms(string query) =>
        TestData.Search("Observation", string.Format(System.Globalization.CultureInfo.InvariantCulture, query, TestData.PatientId)).PatientId
            .ShouldBe(TestData.PatientId.ToString("D"));

    [Fact]
    public void Refuses_a_subject_that_is_not_a_patient() => Error("Observation", "subject=Group/1").StatusCode.ShouldBe(400);

    [Fact]
    public void Searches_one_patient_at_a_time()
    {
        Error("Observation", $"patient={PatientRef},Patient/{TestData.OtherPatientId:D}").StatusCode.ShouldBe(400);
        Error("Observation", $"patient={PatientRef}&subject=Patient/{TestData.OtherPatientId:D}").StatusCode.ShouldBe(400);
    }

    [Fact]
    public void Allergy_intolerance_has_no_subject_parameter() =>
        Error("AllergyIntolerance", $"subject={PatientRef}").Reason.ShouldBe(FhirOutcomes.UnsupportedParameter);

    [Fact]
    public void Rejects_unknown_parameters_instead_of_ignoring_them()
    {
        var error = Error("Observation", $"patient={PatientRef}&dat=2026");

        error.StatusCode.ShouldBe(400);
        error.Reason.ShouldBe(FhirOutcomes.UnsupportedParameter);
        error.Outcome.Issue[0].Code.ShouldBe(Hl7.Fhir.Model.OperationOutcome.IssueType.NotSupported);
    }

    [Fact]
    public void Rejects_modifiers() => Error("Observation", $"patient={PatientRef}&code:text=glucose").Reason.ShouldBe(FhirOutcomes.UnsupportedParameter);

    [Theory]
    [InlineData("date=2026-13-01")]
    [InlineData("date=yesterday")]
    [InlineData("date=sa2026-01-01")]
    [InlineData("date=ge2026-01-01,le2026-02-01")]
    public void Rejects_malformed_dates(string date) => Error("Observation", $"patient={PatientRef}&{date}").StatusCode.ShouldBe(400);

    [Fact]
    public void Accepts_a_date_range_as_two_parameters()
    {
        var request = TestData.Search("Observation", $"patient={PatientRef}&date=ge2026-01-01&date=le2026-03-31");

        request.Criteria.OfType<DateCriterion>().Count().ShouldBe(2);
    }

    [Fact]
    public void Birthdate_takes_a_date_without_time() => Error("Patient", "birthdate=1984-04-12T10:00").StatusCode.ShouldBe(400);

    [Fact]
    public void Patient_searches_need_a_selective_parameter()
    {
        Error("Patient", "gender=female").StatusCode.ShouldBe(400);
        Error("Patient", "_count=10").StatusCode.ShouldBe(400);
        TestData.Search("Patient", "identifier=NH-UH-2600123").PatientId.ShouldBeNull();
    }

    [Fact]
    public void Patient_searches_take_no_patient_parameter() =>
        Error("Patient", $"patient={PatientRef}&name=meera").Reason.ShouldBe(FhirOutcomes.UnsupportedParameter);

    [Fact]
    public void A_name_needs_two_characters() => Error("Patient", "name=a").StatusCode.ShouldBe(400);

    [Theory]
    [InlineData("_format=json")]
    [InlineData("_format=application/fhir%2Bjson")]
    [InlineData("_format=application/json")]
    public void Accepts_json_formats(string format) => TestData.Search("Observation", $"patient={PatientRef}&{format}").ShouldNotBeNull();

    [Fact]
    public void Refuses_xml_with_406() => Error("Observation", $"patient={PatientRef}&_format=xml").StatusCode.ShouldBe(406);

    [Fact]
    public void Reads_pretty_printing()
    {
        TestData.Search("Observation", $"patient={PatientRef}&_pretty=true").Pretty.ShouldBeTrue();
        Error("Observation", $"patient={PatientRef}&_pretty=yes").StatusCode.ShouldBe(400);
    }

    [Fact]
    public void Ignores_empty_parameters() =>
        TestData.Search("Observation", $"patient={PatientRef}&code=&category=vital-signs").Criteria.ShouldHaveSingleItem().Parameter.ShouldBe("category");

    [Fact]
    public void Splits_token_alternatives_and_keeps_systems()
    {
        var criterion = TestData.Search("Observation", $"patient={PatientRef}&code=http://loinc.org|8867-4,9279-1")
            .Criteria.OfType<TokenCriterion>().ShouldHaveSingleItem();

        criterion.AnyOf.Count.ShouldBe(2);
        criterion.AnyOf[0].ShouldBe(new TokenValue("http://loinc.org", "8867-4"));
        criterion.AnyOf[1].ShouldBe(new TokenValue(null, "9279-1"));
    }

    [Fact]
    public void Records_parameter_names_for_the_audit_trail_but_never_values()
    {
        var request = TestData.Search("Patient", "identifier=https://healthid.ndhm.gov.in|91-1234-5678-9012&_count=5");

        request.ParameterNames.ShouldBe(["identifier", "_count"]);
    }
}
