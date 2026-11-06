using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Testing.Abdm;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// The hospital's directory over the FHIR API: its practitioners and facilities, as its ABDM documents carry them, and its
/// wards, rooms and beds, outside every patient's record.
/// </summary>
public sealed class DirectoryTests
{
    private static readonly PractitionerRow Asha = AbdmTestData.Practitioners[0];
    private static readonly FacilityRow Pune = AbdmTestData.Facilities[0];

    private static readonly WardRow Icu = new()
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-00000000c101"),
        FacilityId = TestData.FacilityId,
        Code = "ICU",
        Name = "Intensive Care Unit",
        Type = WardType.Icu,
        CreatedAt = TestData.Now.AddYears(-2),
    };

    private static readonly WardRow SemiPrivate = new()
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-00000000c102"),
        FacilityId = TestData.FacilityId,
        Code = "SPW",
        Name = "Semi-Private Ward",
        Type = WardType.SemiPrivate,
        CreatedAt = TestData.Now.AddYears(-2),
    };

    private static readonly RoomRow Room201 = new()
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-00000000c103"),
        FacilityId = TestData.FacilityId,
        WardId = SemiPrivate.Id,
        Number = "201",
        CreatedAt = TestData.Now.AddYears(-2),
    };

    private static readonly BedRow IcuBed = new()
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-00000000c104"),
        FacilityId = TestData.FacilityId,
        WardId = Icu.Id,
        Label = "ICU-03",
        CreatedAt = TestData.Now.AddYears(-2),
        UpdatedAt = TestData.Now.AddDays(-3),
    };

    private static readonly BedRow RoomBed = new()
    {
        Id = Guid.Parse("01923a4b-0000-7000-8000-00000000c105"),
        FacilityId = TestData.FacilityId,
        WardId = SemiPrivate.Id,
        RoomId = Room201.Id,
        Label = "SPW-201-A",
        CreatedAt = TestData.Now.AddYears(-2),
    };

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
    public void A_facility_taken_out_of_use_is_served_and_found_as_not_active()
    {
        var closed = OrganizationDirectory.Map(Pune with { IsActive = false });

        closed.Active.ShouldBe(false);
        ResourceSearch.Matches(closed, TestData.Search(FhirResourceTypes.Organization, "active=false")).ShouldBeTrue();
        ResourceSearch.Matches(closed, TestData.Search(FhirResourceTypes.Organization, "active=true")).ShouldBeFalse();
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

    [Fact]
    public void A_ward_is_a_place_of_its_facility_of_the_kind_it_is_and_nothing_more()
    {
        var ward = LocationMapper.Ward(Icu);

        (ward.Id, ward.Name, ward.Status, ward.Mode).ShouldBe(
            ($"ward-{Icu.Id:D}", "Intensive Care Unit", (R4.Location.LocationStatus?)R4.Location.LocationStatus.Active, (R4.Location.LocationMode?)R4.Location.LocationMode.Instance));
        ward.Identifier.Select(i => (i.System, i.Value)).ShouldBe([("urn:hc360:ward", "ICU")]);
        ward.Type.ShouldHaveSingleItem().Coding.Select(c => (c.System, c.Code)).ShouldBe([("http://terminology.hl7.org/CodeSystem/v3-RoleCode", "ICU")]);
        ward.PhysicalType.Coding.Select(c => (c.System, c.Code)).ShouldBe([("http://terminology.hl7.org/CodeSystem/location-physical-type", "wa")]);
        ward.ManagingOrganization.Reference.ShouldBe($"Organization/{TestData.FacilityId:D}");
        ward.PartOf.ShouldBeNull();
        (ward.Description, ward.OperationalStatus, ward.Telecom.Count, ward.Address).ShouldBe(((string?)null, (R4.Coding?)null, 0, (R4.Address?)null),
            "what and where a place is, never what happens in it or how to reach it");
        LocationMapper.Ward(Icu with { IsActive = false }).Status.ShouldBe(R4.Location.LocationStatus.Inactive);
    }

    [Fact]
    public void A_room_is_part_of_its_ward_and_a_bed_part_of_its_room_or_of_its_open_ward()
    {
        var room = LocationMapper.Room(Room201, SemiPrivate);
        (room.Id, room.Name, room.PartOf.Reference, room.PhysicalType.Coding[0].Code).ShouldBe(
            ($"room-{Room201.Id:D}", "Semi-Private Ward, room 201", $"Location/ward-{SemiPrivate.Id:D}", "ro"));
        room.Identifier.ShouldBeEmpty("a room's number is unique only in its ward");
        (room.Type[0].Coding[0].Code, room.Type[0].Text).ShouldBe(("HU", "Semi-private ward"));

        var inRoom = LocationMapper.Bed(RoomBed, SemiPrivate, Room201);
        (inRoom.Id, inRoom.Name, inRoom.PartOf.Reference, inRoom.PartOf.Display).ShouldBe(
            ($"bed-{RoomBed.Id:D}", "SPW-201-A", $"Location/room-{Room201.Id:D}", "Semi-Private Ward, room 201"));
        inRoom.Identifier.Select(i => (i.System, i.Value)).ShouldBe([("urn:hc360:bed", "SPW-201-A")]);

        var inWard = LocationMapper.Bed(IcuBed, Icu, null);
        (inWard.PartOf.Reference, inWard.PhysicalType.Coding[0].Code, inWard.Type[0].Coding[0].Code, inWard.Meta.LastUpdated).ShouldBe(
            ($"Location/ward-{Icu.Id:D}", "bd", "ICU", IcuBed.UpdatedAt));
        inWard.ManagingOrganization.Reference.ShouldBe($"Organization/{TestData.FacilityId:D}");
    }

    [Fact]
    public void A_room_or_bed_changes_when_the_ward_or_room_it_takes_its_name_kind_or_place_from_changes()
    {
        var renamedWard = SemiPrivate with { Name = "Semi-Private Wing", UpdatedAt = TestData.Now.AddDays(-1) };
        var renumberedRoom = Room201 with { UpdatedAt = TestData.Now.AddHours(-2) };

        var room = LocationMapper.Room(Room201, renamedWard);
        (room.Name, room.Meta.LastUpdated).ShouldBe(("Semi-Private Wing, room 201", renamedWard.UpdatedAt));
        LocationMapper.Bed(RoomBed, renamedWard, Room201).Meta.LastUpdated.ShouldBe(renamedWard.UpdatedAt);
        LocationMapper.Bed(RoomBed, renamedWard, renumberedRoom).Meta.LastUpdated.ShouldBe(renumberedRoom.UpdatedAt);
        LocationMapper.Bed(IcuBed, Icu, null).Meta.LastUpdated.ShouldBe(IcuBed.UpdatedAt, "its own change, the ward unchanged since");

        // A client syncing by _lastUpdated sees the room again once its ward is renamed.
        const string since = "_lastUpdated=ge2026-09-23T05:00:00Z"; // two days before TestData.Now
        ResourceSearch.Matches(LocationMapper.Room(Room201, SemiPrivate), TestData.Search(FhirResourceTypes.Location, since)).ShouldBeFalse();
        ResourceSearch.Matches(room, TestData.Search(FhirResourceTypes.Location, since)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(WardType.General, "HU", "General ward")]
    [InlineData(WardType.SemiPrivate, "HU", "Semi-private ward")]
    [InlineData(WardType.Private, "HU", "Private ward")]
    [InlineData(WardType.Deluxe, "HU", "Deluxe ward")]
    [InlineData(WardType.Icu, "ICU", "Intensive care unit")]
    [InlineData(WardType.Nicu, "PEDNICU", "Neonatal intensive care unit")]
    [InlineData(WardType.Picu, "PEDICU", "Paediatric intensive care unit")]
    [InlineData(WardType.Hdu, "HU", "High dependency unit")]
    [InlineData(WardType.Maternity, "HU", "Maternity ward")]
    [InlineData(WardType.Isolation, "HU", "Isolation ward")]
    [InlineData(WardType.DayCare, "HU", "Day care ward")]
    [InlineData(WardType.Emergency, "ER", "Emergency ward")]
    public void A_ward_is_coded_as_the_most_specific_service_delivery_location_type_there_is(WardType type, string code, string text)
    {
        var concept = LocationMapper.ServiceType(type);

        (concept.Coding.ShouldHaveSingleItem().System, concept.Coding[0].Code, concept.Text).ShouldBe(("http://terminology.hl7.org/CodeSystem/v3-RoleCode", code, text));
    }

    [Fact]
    public void A_place_is_found_by_what_it_is_part_of_its_facility_its_kind_whether_in_use_and_its_label()
    {
        var bed = LocationMapper.Bed(RoomBed, SemiPrivate, Room201);
        bool Found(string query) => ResourceSearch.Matches(bed, TestData.Search(FhirResourceTypes.Location, query));

        Found($"partof=Location/room-{Room201.Id:D}").ShouldBeTrue();
        Found($"partof=room-{Room201.Id:D}").ShouldBeTrue();
        Found($"partof=https://hc360.example/fhir/r4/Location/room-{Room201.Id:D}").ShouldBeTrue();
        Found($"partof=Location/ward-{SemiPrivate.Id:D}").ShouldBeFalse("a bed in a room is part of the room");
        Found($"organization=Organization/{TestData.FacilityId:D}").ShouldBeTrue();
        Found($"organization={Guid.CreateVersion7():D}").ShouldBeFalse();
        Found("type=HU").ShouldBeTrue();
        Found("type=http://terminology.hl7.org/CodeSystem/v3-RoleCode|ICU").ShouldBeFalse();
        Found("status=active").ShouldBeTrue();
        Found("status=inactive").ShouldBeFalse();
        Found("identifier=urn:hc360:bed|SPW-201-A").ShouldBeTrue();
        Found("identifier=urn:hc360:ward|SPW-201-A").ShouldBeFalse();
        Found("name=spw").ShouldBeTrue();
        TestData.Search(FhirResourceTypes.Location, string.Empty).Criteria.ShouldBeEmpty("the places may be listed");
    }
}
