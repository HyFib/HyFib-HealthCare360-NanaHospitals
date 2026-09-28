using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class ResourceSearchTests
{
    private static readonly string Patient = $"patient=Patient/{TestData.PatientId:D}";

    /// <summary>Two vital-sign sets (Monday and Wednesday) and one released glucose result (Tuesday).</summary>
    private static List<R4.Resource> Observations()
    {
        var monday = new VitalSignsRow
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-000000003001"),
            PatientId = TestData.PatientId,
            RecordedAt = new DateTimeOffset(2026, 9, 21, 5, 0, 0, TimeSpan.Zero),
            Pulse = 80,
            SystolicBp = 130,
            DiastolicBp = 85,
            CreatedAt = new DateTimeOffset(2026, 9, 21, 5, 0, 0, TimeSpan.Zero),
        };
        var wednesday = monday with
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-000000003002"),
            RecordedAt = new DateTimeOffset(2026, 9, 23, 5, 0, 0, TimeSpan.Zero),
            SystolicBp = null,
            DiastolicBp = null,
            CreatedAt = new DateTimeOffset(2026, 9, 23, 5, 0, 0, TimeSpan.Zero),
        };
        var glucose = new LabResultRow
        {
            Id = Guid.Parse("01923a4b-0000-7000-8000-000000003003"),
            PatientId = TestData.PatientId,
            TestCode = "FBS",
            TestName = "Fasting blood sugar",
            Loinc = "1558-6",
            ResultType = LabResultType.Numeric,
            ValueText = "96",
            Unit = "mg/dL",
            OrderedAt = new DateTimeOffset(2026, 9, 22, 3, 0, 0, TimeSpan.Zero),
            CollectedAt = new DateTimeOffset(2026, 9, 22, 3, 30, 0, TimeSpan.Zero),
            VerifiedAt = new DateTimeOffset(2026, 9, 22, 6, 0, 0, TimeSpan.Zero),
            ReleasedAt = new DateTimeOffset(2026, 9, 22, 6, 0, 0, TimeSpan.Zero),
            CreatedAt = new DateTimeOffset(2026, 9, 22, 3, 30, 0, TimeSpan.Zero),
        };

        return [.. VitalSignObservations.Map(monday), .. VitalSignObservations.Map(wednesday), LabObservations.Map(glucose)];
    }

    private static SearchPage Search(string query, IEnumerable<R4.Resource>? resources = null) =>
        ResourceSearch.Execute(resources ?? Observations(), TestData.Search("Observation", $"{Patient}&{query}"));

    [Fact]
    public void Returns_everything_newest_first_with_the_total()
    {
        var page = Search("_count=100");

        page.Total.ShouldBe(4);
        page.Resources.Select(r => ((R4.Observation)r).Code.Coding[0].Code).ShouldBe(["8867-4", "1558-6", "85354-9", "8867-4"]);
    }

    [Fact]
    public void Filters_by_category()
    {
        Search("category=vital-signs").Total.ShouldBe(3);
        Search("category=laboratory").Resources.ShouldHaveSingleItem().Id.ShouldStartWith("lab-");
        Search("category=http://terminology.hl7.org/CodeSystem/observation-category|laboratory").Total.ShouldBe(1);
        Search("category=vital-signs,laboratory").Total.ShouldBe(4);
    }

    [Fact]
    public void Filters_by_loinc_code()
    {
        Search("code=http://loinc.org|8867-4").Total.ShouldBe(2);
        Search("code=85354-9").Total.ShouldBe(1);
        Search("code=http://snomed.info/sct|8867-4").Total.ShouldBe(0);
    }

    [Fact]
    public void Filters_by_effective_date_in_ist()
    {
        Search("date=2026-09-22").Resources.ShouldHaveSingleItem().Id.ShouldStartWith("lab-");
        Search("date=ge2026-09-22").Total.ShouldBe(2);
        Search("date=ge2026-09-22&date=lt2026-09-23").Total.ShouldBe(1);
        Search("date=lt2026-09-22").Total.ShouldBe(2);
    }

    [Fact]
    public void Filters_by_last_update()
    {
        Search("_lastUpdated=ge2026-09-23").Total.ShouldBe(1);
        Search("_lastUpdated=gt2026-09-25").Total.ShouldBe(0);
    }

    [Fact]
    public void Leaves_out_entries_in_error_unless_asked_for_by_status()
    {
        var resources = Observations();
        ((R4.Observation)resources[0]).Status = R4.ObservationStatus.EnteredInError;

        Search("_count=100", resources).Total.ShouldBe(3);
        Search("status=entered-in-error", resources).Total.ShouldBe(1);
        Search("status=final,entered-in-error", resources).Total.ShouldBe(4);
    }

    [Fact]
    public void Pages_with_count_and_offset()
    {
        var first = Search("_count=3");
        var second = Search("_count=3&_offset=3");

        first.Total.ShouldBe(4);
        first.Resources.Count.ShouldBe(3);
        second.Resources.Count.ShouldBe(1);
        first.Resources.Concat(second.Resources).Select(r => r.Id).Distinct().Count().ShouldBe(4);
    }

    [Fact]
    public void A_count_of_zero_returns_only_the_total()
    {
        var page = Search("_count=0");

        page.Total.ShouldBe(4);
        page.Resources.ShouldBeEmpty();
    }

    [Fact]
    public void An_offset_past_the_end_returns_an_empty_page() => Search("_offset=50").Resources.ShouldBeEmpty();

    [Fact]
    public void Searches_by_id() =>
        Search("_id=lab-01923a4b-0000-7000-8000-000000003003").Resources.ShouldHaveSingleItem().Id.ShouldBe("lab-01923a4b-0000-7000-8000-000000003003");

    [Fact]
    public void Filters_conditions_by_clinical_status_and_icd10_code()
    {
        var diabetes = ConditionMapper.Map(new DiagnosisRow
        {
            Id = Guid.CreateVersion7(),
            PatientId = TestData.PatientId,
            IcdCode = "E11.9",
            Title = "Type 2 diabetes mellitus",
            Type = DiagnosisType.Final,
            Status = DiagnosisStatus.Chronic,
            RecordedAt = TestData.Now,
            CreatedAt = TestData.Now,
        });
        var fever = ConditionMapper.Map(new DiagnosisRow
        {
            Id = Guid.CreateVersion7(),
            PatientId = TestData.PatientId,
            Title = "Viral fever",
            Type = DiagnosisType.Provisional,
            Status = DiagnosisStatus.Resolved,
            RecordedAt = TestData.Now.AddDays(-30),
            CreatedAt = TestData.Now.AddDays(-30),
        });
        R4.Resource[] conditions = [diabetes, fever];

        ResourceSearch.Execute(conditions, TestData.Search("Condition", $"{Patient}&clinical-status=active")).Resources.ShouldBe([diabetes]);
        ResourceSearch.Execute(conditions, TestData.Search("Condition", $"{Patient}&code=http://hl7.org/fhir/sid/icd-10|e11.9")).Total.ShouldBe(1);
        ResourceSearch.Execute(conditions, TestData.Search("Condition", $"{Patient}&category=problem-list-item")).Total.ShouldBe(2);
        ResourceSearch.Execute(conditions, TestData.Search("Condition", $"{Patient}&recorded-date=lt2026-09-01")).Resources.ShouldBe([fever]);
    }
}

public sealed class SearchBundlesTests
{
    private const string Base = "https://hc360.example/fhir/r4";

    private static readonly string Patient = $"patient=Patient/{TestData.PatientId:D}";

    private static List<R4.Resource> Resources(int count) =>
    [
        .. Enumerable.Range(1, count).Select(i => (R4.Resource)new R4.Observation
        {
            Id = $"lab-01923a4b-0000-7000-8000-{i:D12}",
            Status = R4.ObservationStatus.Final,
        }),
    ];

    private static R4.Bundle Bundle(string query, int total, R4.OperationOutcome? outcome = null)
    {
        var request = TestData.Search("Observation", $"{Patient}&{query}");
        var page = ResourceSearch.Execute(Resources(total), request);
        return SearchBundles.Build(Base, request, page, TestData.Now, outcome);
    }

    private static string? Link(R4.Bundle bundle, string relation) => bundle.Link.SingleOrDefault(l => l.Relation == relation)?.Url;

    [Fact]
    public void Is_a_searchset_with_the_total_and_match_entries()
    {
        var bundle = Bundle("_count=10", 3);

        bundle.Type.ShouldBe(R4.Bundle.BundleType.Searchset);
        bundle.Total.ShouldBe(3);
        bundle.Entry.Count.ShouldBe(3);
        bundle.Entry.ShouldAllBe(e => e.Search.Mode == R4.Bundle.SearchEntryMode.Match);
        bundle.Entry[0].FullUrl.ShouldBe($"{Base}/Observation/{bundle.Entry[0].Resource.Id}");
    }

    [Fact]
    public void The_self_link_shows_the_parameters_and_the_capped_page_size()
    {
        var self = Link(Bundle("_count=500&category=laboratory", 3), "self");

        self.ShouldBe($"{Base}/Observation?patient=Patient%2F{TestData.PatientId:D}&category=laboratory&_count=100");
    }

    [Fact]
    public void Links_to_the_next_page_until_the_last()
    {
        var first = Bundle("_count=10", 25);
        Link(first, "next").ShouldEndWith("&_count=10&_offset=10");
        Link(first, "previous").ShouldBeNull();

        var middle = Bundle("_count=10&_offset=10", 25);
        Link(middle, "next").ShouldEndWith("&_count=10&_offset=20");
        Link(middle, "previous").ShouldEndWith("&_count=10");

        var last = Bundle("_count=10&_offset=20", 25);
        last.Entry.Count.ShouldBe(5);
        Link(last, "next").ShouldBeNull();
        Link(last, "previous").ShouldEndWith("&_count=10&_offset=10");
    }

    [Fact]
    public void A_single_page_has_no_next_link() => Link(Bundle("_count=10", 10), "next").ShouldBeNull();

    [Fact]
    public void A_count_of_zero_has_no_paging_links()
    {
        var bundle = Bundle("_count=0", 25);

        bundle.Total.ShouldBe(25);
        bundle.Entry.ShouldBeEmpty();
        Link(bundle, "next").ShouldBeNull();
    }

    [Fact]
    public void Escapes_parameter_values_in_links() =>
        Link(Bundle("code=http://loinc.org|8867-4", 1), "self").ShouldContain("code=http%3A%2F%2Floinc.org%7C8867-4");

    [Fact]
    public void Adds_an_outcome_entry_without_counting_it()
    {
        var bundle = Bundle("_count=10", 0, FhirOutcomes.Withheld());

        bundle.Total.ShouldBe(0);
        var entry = bundle.Entry.ShouldHaveSingleItem();
        entry.Search.Mode.ShouldBe(R4.Bundle.SearchEntryMode.Outcome);
        var issue = entry.Resource.ShouldBeOfType<R4.OperationOutcome>().Issue.ShouldHaveSingleItem();
        issue.Code.ShouldBe(R4.OperationOutcome.IssueType.Suppressed);
        issue.Severity.ShouldBe(R4.OperationOutcome.IssueSeverity.Information);
    }
}
