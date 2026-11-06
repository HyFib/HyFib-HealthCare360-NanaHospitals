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
    public void Sorts_by_code_then_newest_first_and_by_id()
    {
        // A vital sign's code is its LOINC code: blood pressure 85354-9, pulse 8867-4. The glucose result carries LOINC 1558-6
        // and the lab's own code FBS: the least of them counts in increasing order, the greatest in decreasing order. The
        // two pulses tie, and fall back to the default order, newest first.
        Search("_sort=code").Resources.Select(Code).ShouldBe(["1558-6", "85354-9", "8867-4 Wed", "8867-4 Mon"]);
        Search("_sort=-code").Resources.Select(Code).ShouldBe(["1558-6", "8867-4 Wed", "8867-4 Mon", "85354-9"]);
        Search("_sort=code,date").Resources.Select(Code).ShouldBe(["1558-6", "85354-9", "8867-4 Mon", "8867-4 Wed"]);
    }

    [Fact]
    public void Sorts_by_date_oldest_first_and_the_same_time_by_id()
    {
        var monday = Search("_sort=date").Resources.Take(2).Select(r => r.Id).ToList();

        Search("_sort=date").Resources.Select(Code).ShouldBe(["85354-9", "8867-4 Mon", "1558-6", "8867-4 Wed"]);
        monday.ShouldBe(monday.Order(StringComparer.Ordinal));
        Search("_sort=-date").Resources.Select(Code).ShouldBe(["8867-4 Wed", "1558-6", "85354-9", "8867-4 Mon"]);
    }

    [Fact]
    public void A_resource_without_the_value_comes_last_whichever_the_order()
    {
        var resources = Observations();
        var undated = (R4.Observation)resources.Single(r => r is R4.Observation { Code.Coding: [{ Code: "1558-6" }, ..] });
        undated.Effective = null;

        Search("_sort=date", resources).Resources[^1].ShouldBeSameAs(undated);
        Search("_sort=-date", resources).Resources[^1].ShouldBeSameAs(undated);
    }

    [Fact]
    public void Pages_through_the_asked_order()
    {
        var all = Search("_sort=-code&_count=100").Resources.Select(r => r.Id).ToList();
        var pages = new[] { 0, 2 }.SelectMany(offset => Search($"_sort=-code&_count=2&_offset={offset}").Resources).Select(r => r.Id).ToList();

        pages.ShouldBe(all);
    }

    [Fact]
    public void A_period_counts_by_its_start_in_increasing_order_and_by_its_end_in_decreasing_order()
    {
        R4.Encounter Stay(string id, string start, string? end) =>
            new() { Id = id, Period = new R4.Period { Start = start, End = end }, Status = R4.Encounter.EncounterStatus.Finished };
        List<R4.Resource> stays = [Stay("long", "2026-09-01", "2026-09-10"), Stay("short", "2026-09-03", "2026-09-04"), Stay("ongoing", "2026-09-05", null)];

        Ids(ResourceSearch.Execute(stays, TestData.Search("Encounter", $"{Patient}&_sort=date"))).ShouldBe(["long", "short", "ongoing"]);
        Ids(ResourceSearch.Execute(stays, TestData.Search("Encounter", $"{Patient}&_sort=-date"))).ShouldBe(["ongoing", "long", "short"]);
        Ids(ResourceSearch.Execute(stays, TestData.Search("Encounter", Patient))).ShouldBe(["ongoing", "short", "long"]);
    }

    [Fact]
    public void Sorts_people_by_family_name_then_given_names_whatever_their_case()
    {
        R4.Practitioner Doctor(string id, string given, string family) =>
            new() { Id = id, Name = [new R4.HumanName { Text = $"Dr. {given} {family}", Family = family, Given = [given], Prefix = ["Dr."] }] };
        List<R4.Resource> doctors = [Doctor("p1", "Asha", "Rao"), Doctor("p2", "Ravi", "Kumar"), Doctor("p3", "anil", "Kumar")];

        Ids(ResourceSearch.Execute(doctors, TestData.Search("Practitioner", "_id=p1,p2,p3&_sort=name"))).ShouldBe(["p3", "p2", "p1"]);
        Ids(ResourceSearch.Execute(doctors, TestData.Search("Practitioner", "_id=p1,p2,p3&_sort=-name"))).ShouldBe(["p1", "p2", "p3"]);
    }

    [Fact]
    public void Sorts_places_and_facilities_by_name_whatever_its_case_and_codes_as_written()
    {
        // Lower-cased, "general ward" comes after "Emergency" and before "ICU"; as written it would come last ('g' > 'I').
        // The ids are in the other order, so an order that fell back to them would show.
        List<R4.Resource> places =
        [
            new R4.Location { Id = "l1", Name = "ICU", Type = [new R4.CodeableConcept("http://example.org/kind", "b")] },
            new R4.Location { Id = "l2", Name = "general ward", Type = [new R4.CodeableConcept("http://example.org/kind", "a")] },
            new R4.Location { Id = "l3", Name = "Emergency", Type = [new R4.CodeableConcept("http://example.org/kind", "C")] },
        ];
        List<R4.Resource> facilities =
            [new R4.Organization { Id = "o1", Name = "north wing" }, new R4.Organization { Id = "o2", Name = "Main campus" }];

        Ids(ResourceSearch.Execute(places, TestData.Search("Location", "_sort=name"))).ShouldBe(["l3", "l2", "l1"]);
        Ids(ResourceSearch.Execute(places, TestData.Search("Location", "_sort=-name"))).ShouldBe(["l1", "l2", "l3"]);
        Ids(ResourceSearch.Execute(facilities, TestData.Search("Organization", "_sort=name"))).ShouldBe(["o2", "o1"]);

        // A code is compared as written, character by character: "C" before "a" and "b". Lower-cased, it would be last.
        Ids(ResourceSearch.Execute(places, TestData.Search("Location", "_sort=type"))).ShouldBe(["l3", "l2", "l1"]);
    }

    private static string Code(R4.Resource resource)
    {
        var observation = (R4.Observation)resource;
        var code = observation.Code.Coding[0].Code!;
        return code == "8867-4" ? $"{code} {(observation.Effective.ToString()!.StartsWith("2026-09-23", StringComparison.Ordinal) ? "Wed" : "Mon")}" : code;
    }

    private static List<string> Ids(SearchPage page) => [.. page.Resources.Select(r => r.Id)];

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
