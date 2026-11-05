using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Testing.Abdm;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// The hospital's directory over the FHIR API: its practitioners and facilities, as its ABDM documents carry them, outside
/// every patient's record.
/// </summary>
public sealed class DirectoryTests
{
    private static readonly PractitionerRow Asha = AbdmTestData.Practitioners[0];
    private static readonly FacilityRow Pune = AbdmTestData.Facilities[0];

    [Fact]
    public void A_practitioner_is_served_as_the_abdm_documents_carry_them_and_says_whether_they_are_in_use()
    {
        var practitioner = PractitionerDirectory.Map(Asha with { IsActive = false });

        (practitioner.Id, practitioner.Active).ShouldBe((FhirIds.Of(Asha.Id), (bool?)false));
        practitioner.Name.ShouldHaveSingleItem().Text.ShouldBe("Dr. Asha Rao");
        practitioner.Identifier.Select(i => (i.System, i.Value)).ShouldContain((FhirSystems.Hpr, AbdmTestData.HprId));
        practitioner.Telecom.ShouldBeEmpty("who a practitioner is, never how to reach them");
        var validation = AbdmProfileValidator.Validate(practitioner, AbdmProfiles.Practitioner);
        validation.Errors.ShouldBeEmpty(validation.Describe());
    }

    [Fact]
    public void A_facility_is_served_as_the_abdm_documents_carry_it()
    {
        var organization = OrganizationDirectory.Map(Pune);

        (organization.Id, organization.Name, organization.Active).ShouldBe((FhirIds.Of(Pune.Id), "Nana Hospitals, Pune", (bool?)true));
        organization.Identifier.Select(i => (i.System, i.Value)).ShouldContain((FhirSystems.Hfr, "IN2710000123"));
        var validation = AbdmProfileValidator.Validate(organization, AbdmProfiles.Organization);
        validation.Errors.ShouldBeEmpty(validation.Describe());
    }

    [Fact]
    public void The_directory_is_in_no_patients_record()
    {
        foreach (var type in FhirResourceTypes.Directory)
        {
            FhirResourceTypes.IsServed(type).ShouldBeTrue();
            SearchDefinitions.Find(type).ShouldNotBeNull().IsPatientScoped.ShouldBeFalse();
        }

        FhirResourceTypes.IsDirectory(FhirResourceTypes.Patient).ShouldBeFalse();
        SearchDefinitions.Find(FhirResourceTypes.Procedure).ShouldNotBeNull().IsPatientScoped.ShouldBeTrue();
    }

    [Fact]
    public void A_practitioner_is_looked_up_never_listed_and_the_facilities_may_be_listed()
    {
        var listed = SearchRequestParser.Parse(SearchDefinitions.Find(FhirResourceTypes.Practitioner)!, TestData.Query("active=true"));
        listed.Error.ShouldNotBeNull().Outcome.Issue[0].Diagnostics.ShouldBe("Practitioner searches need at least one of: _id, identifier, name.");

        TestData.Search(FhirResourceTypes.Practitioner, "name=asha&active=true").Criteria.Count.ShouldBe(2);
        TestData.Search(FhirResourceTypes.Organization, string.Empty).Criteria.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("name=asha", true)]
    [InlineData("name=ASHA%20r", true)]
    [InlineData("name=rao%20asha", true)]
    [InlineData("name=sha", false)]
    [InlineData("name=asha%20k", false)]
    [InlineData("name=asha&active=true", true)]
    [InlineData("name=asha&active=false", false)]
    [InlineData("identifier=https://doctor.ndhm.gov.in|21-1521-3828-3227", true)]
    [InlineData("identifier=21-1521-3828-3227", true)]
    [InlineData("identifier=https://facility.ndhm.gov.in|21-1521-3828-3227", false)]
    [InlineData("identifier=urn:hc360:medical-council-registration|MMC-2009-041234", true)]
    public void A_practitioner_is_found_by_the_words_of_the_name_an_identifier_and_whether_they_are_in_use(string query, bool found) =>
        ResourceSearch.Matches(PractitionerDirectory.Map(Asha), TestData.Search(FhirResourceTypes.Practitioner, query)).ShouldBe(found);

    [Theory]
    [InlineData("name=nana%20pune", true)]
    [InlineData("name=pune", true)]
    [InlineData("name=mumbai", false)]
    [InlineData("identifier=https://facility.ndhm.gov.in|IN2710000123", true)]
    [InlineData("identifier=urn:hc360:facility|NHP", true)]
    [InlineData("active=false", false)]
    public void A_facility_is_found_by_the_words_of_its_name_and_its_identifiers(string query, bool found) =>
        ResourceSearch.Matches(OrganizationDirectory.Map(Pune), TestData.Search(FhirResourceTypes.Organization, query)).ShouldBe(found);
}
