using HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests.Abdm;

/// <summary>A document stands on its own: the Composition first, every entry once, every reference inside it.</summary>
public sealed class DocumentBundleBuilderTests
{
    private static readonly Guid CompositionId = Guid.Parse("01923a4b-0000-7000-8000-00000000c001");

    private static R4.Composition Composition(R4.ResourceReference subject) => new()
    {
        Status = R4.CompositionStatus.Final,
        Type = new R4.CodeableConcept { Text = "Note" },
        Subject = subject,
        Date = "2026-09-25",
        Title = "Note",
    };

    [Fact]
    public void Entries_are_urn_uuid_resources_claiming_their_profile()
    {
        var bundle = new DocumentBundleBuilder()
            .Add(CompositionId, Composition(DocumentBundleBuilder.Reference(TestData.PatientId)), "https://example.org/Composition")
            .Add(TestData.PatientId, new R4.Patient(), "https://example.org/Patient")
            .Build(AbdmTestData.DocumentId, TestData.Now);

        bundle.Entry.Select(e => e.FullUrl).ShouldBe([$"urn:uuid:{CompositionId:D}", $"urn:uuid:{TestData.PatientId:D}"]);
        bundle.Entry[1].Resource.Id.ShouldBe(TestData.PatientId.ToString("D"));
        bundle.Entry[1].Resource.Meta.Profile.ShouldBe(["https://example.org/Patient"]);
        bundle.Id.ShouldBe(AbdmTestData.DocumentId.ToString("D"));
        bundle.Timestamp.ShouldBe(TestData.Now);
    }

    [Fact]
    public void A_document_starts_with_its_composition() =>
        Should.Throw<InvalidOperationException>(() => new DocumentBundleBuilder()
            .Add(TestData.PatientId, new R4.Patient(), "https://example.org/Patient")
            .Add(CompositionId, Composition(DocumentBundleBuilder.Reference(TestData.PatientId)), "https://example.org/Composition")
            .Build(AbdmTestData.DocumentId, TestData.Now));

    [Fact]
    public void A_reference_to_a_resource_outside_the_document_is_refused()
    {
        var builder = new DocumentBundleBuilder()
            .Add(CompositionId, Composition(new R4.ResourceReference($"Patient/{TestData.PatientId:D}")), "https://example.org/Composition")
            .Add(TestData.PatientId, new R4.Patient(), "https://example.org/Patient");

        Should.Throw<InvalidOperationException>(() => builder.Build(AbdmTestData.DocumentId, TestData.Now)).Message
            .ShouldContain("not in the document");
    }

    [Fact]
    public void A_reference_that_only_names_its_target_is_allowed()
    {
        var composition = Composition(DocumentBundleBuilder.Reference(TestData.PatientId));
        composition.Author.Add(new R4.ResourceReference { Display = "Dr. Asha Rao" });

        var bundle = new DocumentBundleBuilder()
            .Add(CompositionId, composition, "https://example.org/Composition")
            .Add(TestData.PatientId, new R4.Patient(), "https://example.org/Patient")
            .Build(AbdmTestData.DocumentId, TestData.Now);

        bundle.Entry.Count.ShouldBe(2);
    }

    [Fact]
    public void Two_entries_may_not_share_an_id() =>
        Should.Throw<InvalidOperationException>(() => new DocumentBundleBuilder()
            .Add(CompositionId, Composition(DocumentBundleBuilder.Reference(TestData.PatientId)), "https://example.org/Composition")
            .Add(TestData.PatientId, new R4.Patient(), "https://example.org/Patient")
            .Add(TestData.PatientId, new R4.Patient(), "https://example.org/Patient")
            .Build(AbdmTestData.DocumentId, TestData.Now));

    [Fact]
    public void Derived_ids_are_stable_distinct_version_8_uuids()
    {
        var id = DocumentBundleBuilder.DerivedId(AbdmTestData.SummaryId, "Condition J18.9");

        id.ShouldBe(DocumentBundleBuilder.DerivedId(AbdmTestData.SummaryId, "Condition J18.9"));
        id.ShouldNotBe(DocumentBundleBuilder.DerivedId(AbdmTestData.SummaryId, "Condition E11.9"));
        id.ShouldNotBe(DocumentBundleBuilder.DerivedId(TestData.PatientId, "Condition J18.9"));
        var text = id.ToString("D");
        text[14].ShouldBe('8', "version 8 (RFC 9562)");
        "89ab".ShouldContain(text[19], "the RFC 4122 variant");
    }
}
