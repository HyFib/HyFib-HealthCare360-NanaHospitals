using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.SharedKernel;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class PatientMapperTests
{
    private static R4.Patient Map(PatientRow row) => PatientMapper.Map(row, TestData.Directory);

    [Fact]
    public void Uses_the_patient_guid_as_id_and_the_audit_timestamp_as_last_updated()
    {
        var patient = Map(TestData.Patient());

        patient.Id.ShouldBe("01923a4b-0000-7000-8000-000000000001");
        patient.Meta.LastUpdated.ShouldBe(TestData.Now.AddDays(-3));
        patient.Meta.LastUpdated!.Value.Offset.ShouldBe(TimeSpan.FromMinutes(330));
    }

    [Fact]
    public void Falls_back_to_the_creation_time_when_never_updated()
    {
        var patient = Map(TestData.Patient() with { UpdatedAt = null });

        patient.Meta.LastUpdated.ShouldBe(TestData.Now.AddYears(-1));
    }

    [Fact]
    public void Identifies_the_patient_by_uhid()
    {
        var uhid = Map(TestData.Patient()).Identifier.ShouldHaveSingleItem();

        uhid.System.ShouldBe("urn:hc360:uhid");
        uhid.Value.ShouldBe("NH-UH-2600123");
        uhid.Use.ShouldBe(R4.Identifier.IdentifierUse.Usual);
        uhid.Type.Coding.ShouldHaveSingleItem().Code.ShouldBe("MR");
    }

    [Fact]
    public void Adds_the_abha_number_as_an_abdm_identifier_formatted_in_groups()
    {
        var patient = Map(TestData.Patient() with { AbhaNumber = "91123456789012" });

        var abha = patient.Identifier.Single(i => i.System == "https://healthid.ndhm.gov.in");
        abha.Value.ShouldBe("91-1234-5678-9012");
        abha.Use.ShouldBe(R4.Identifier.IdentifierUse.Official);
        abha.Type.Text.ShouldBe("ABHA number");
    }

    [Fact]
    public void Leaves_out_the_abha_identifier_when_there_is_none() =>
        Map(TestData.Patient() with { AbhaNumber = "  " }).Identifier.ShouldNotContain(i => i.System == FhirSystems.Abha);

    [Theory]
    [InlineData(Gender.Male, R4.AdministrativeGender.Male)]
    [InlineData(Gender.Female, R4.AdministrativeGender.Female)]
    [InlineData(Gender.Other, R4.AdministrativeGender.Other)]
    [InlineData(Gender.Unknown, R4.AdministrativeGender.Unknown)]
    public void Maps_every_gender(Gender gender, R4.AdministrativeGender expected) =>
        Map(TestData.Patient() with { Gender = gender }).Gender.ShouldBe(expected);

    [Fact]
    public void Publishes_an_exact_birth_date() => Map(TestData.Patient()).BirthDate.ShouldBe("1984-04-12");

    [Fact]
    public void Publishes_only_the_year_of_an_estimated_birth_date() =>
        Map(TestData.Patient() with { IsDateOfBirthEstimated = true }).BirthDate.ShouldBe("1984");

    [Fact]
    public void Omits_an_unknown_birth_date() => Map(TestData.Patient() with { DateOfBirth = null }).BirthDate.ShouldBeNull();

    [Fact]
    public void Shows_the_full_phone_number_first_then_the_alternate_and_email()
    {
        var telecom = Map(TestData.Patient() with { AlternatePhone = "+912024567890" }).Telecom;

        telecom.Count.ShouldBe(3);
        telecom[0].System.ShouldBe(R4.ContactPoint.ContactPointSystem.Phone);
        telecom[0].Value.ShouldBe("+919876543210");
        telecom[0].Use.ShouldBe(R4.ContactPoint.ContactPointUse.Mobile);
        telecom[0].Rank.ShouldBe(1);
        telecom[1].Value.ShouldBe("+912024567890");
        telecom[1].Use.ShouldBe(R4.ContactPoint.ContactPointUse.Home);
        telecom[2].System.ShouldBe(R4.ContactPoint.ContactPointSystem.Email);
    }

    [Fact]
    public void Has_no_telecom_when_no_contact_details_are_on_file() =>
        Map(TestData.Patient() with { Phone = null, Email = null }).Telecom.ShouldBeEmpty();

    [Fact]
    public void Splits_the_name_into_parts()
    {
        var name = Map(TestData.Patient()).Name.ShouldHaveSingleItem();

        name.Use.ShouldBe(R4.HumanName.NameUse.Official);
        name.Text.ShouldBe("Meera Anil Kulkarni");
        name.Family.ShouldBe("Kulkarni");
        name.Given.ShouldBe(["Meera", "Anil"]);
        name.Prefix.ShouldBe(["Mrs"]);
    }

    [Fact]
    public void Keeps_a_single_name_without_a_family_name()
    {
        var name = Map(TestData.Patient() with { MiddleName = null, LastName = null, FullName = "Meera", Title = null }).Name.ShouldHaveSingleItem();

        name.Family.ShouldBeNull();
        name.Given.ShouldBe(["Meera"]);
        name.Prefix.ShouldBeEmpty();
    }

    [Fact]
    public void Maps_the_indian_address_with_pin_code()
    {
        var address = Map(TestData.Patient()).Address.ShouldHaveSingleItem();

        address.Line.ShouldBe(["12 MG Road"]);
        address.City.ShouldBe("Pune");
        address.District.ShouldBe("Pune");
        address.State.ShouldBe("Maharashtra");
        address.PostalCode.ShouldBe("411001");
        address.Country.ShouldBe("India");
    }

    [Fact]
    public void Omits_an_address_that_holds_only_the_default_country() =>
        Map(TestData.Patient() with { Address = new Address() }).Address.ShouldBeEmpty();

    [Theory]
    [InlineData(MaritalStatus.Single, "S")]
    [InlineData(MaritalStatus.Married, "M")]
    [InlineData(MaritalStatus.Divorced, "D")]
    [InlineData(MaritalStatus.Widowed, "W")]
    [InlineData(MaritalStatus.Separated, "L")]
    [InlineData(MaritalStatus.Unknown, "UNK")]
    public void Codes_marital_status_with_hl7_v3(MaritalStatus status, string code) =>
        Map(TestData.Patient() with { MaritalStatus = status }).MaritalStatus.Coding.ShouldHaveSingleItem().Code.ShouldBe(code);

    [Fact]
    public void States_death_with_its_time_when_known()
    {
        Map(TestData.Patient() with { IsDeceased = true, DeceasedAt = TestData.Now }).Deceased
            .ShouldBeOfType<R4.FhirDateTime>().Value.ShouldBe("2026-09-25T10:30:00+05:30");
        Map(TestData.Patient() with { IsDeceased = true }).Deceased.ShouldBeOfType<R4.FhirBoolean>().Value.ShouldBe(true);
        Map(TestData.Patient()).Deceased.ShouldBeNull();
    }

    [Fact]
    public void A_merged_record_is_not_active()
    {
        Map(TestData.Patient()).Active.ShouldBe(true);
        Map(TestData.Patient() with { MergedIntoPatientId = TestData.OtherPatientId }).Active.ShouldBe(false);
        Map(TestData.Patient() with { IsActive = false }).Active.ShouldBe(false);
    }

    [Fact]
    public void Lists_the_emergency_contact_and_the_guardian()
    {
        var patient = Map(TestData.Patient() with
        {
            EmergencyContactName = "Anil Kulkarni",
            EmergencyContactPhone = "+919812345678",
            EmergencyContactRelation = "Husband",
            GuardianName = "Anil Kulkarni",
            GuardianRelation = GuardianRelation.WifeOf,
        });

        patient.Contact.Count.ShouldBe(2);
        var emergency = patient.Contact[0];
        emergency.Relationship.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("C");
        emergency.Relationship[0].Text.ShouldBe("Husband");
        emergency.Telecom.ShouldHaveSingleItem().Value.ShouldBe("+919812345678");
        patient.Contact[1].Relationship.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("HUSB");
    }

    [Theory]
    [InlineData(GuardianRelation.SonOf, "PRN")]
    [InlineData(GuardianRelation.DaughterOf, "PRN")]
    [InlineData(GuardianRelation.WifeOf, "HUSB")]
    [InlineData(GuardianRelation.HusbandOf, "WIFE")]
    [InlineData(GuardianRelation.FatherOf, "CHILD")]
    [InlineData(GuardianRelation.MotherOf, "CHILD")]
    [InlineData(GuardianRelation.Guardian, "GUARD")]
    public void Codes_the_guardian_role_towards_the_patient(GuardianRelation relation, string code) =>
        PatientMapper.GuardianRole(relation).Coding.ShouldHaveSingleItem().Code.ShouldBe(code);

    [Theory]
    [InlineData("Hindi", "hi")]
    [InlineData("tamil", "ta")]
    [InlineData("English", "en")]
    public void Codes_the_preferred_language_with_bcp47(string language, string code)
    {
        var communication = Map(TestData.Patient() with { PreferredLanguage = language }).Communication.ShouldHaveSingleItem();

        communication.Preferred.ShouldBe(true);
        communication.Language.Coding.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact]
    public void Keeps_an_unlisted_language_as_text() =>
        Map(TestData.Patient() with { PreferredLanguage = "Konkani" }).Communication.ShouldHaveSingleItem().Language.Text.ShouldBe("Konkani");

    [Fact]
    public void Names_the_registering_hospital_with_its_hfr_id()
    {
        var organization = Map(TestData.Patient()).ManagingOrganization.ShouldNotBeNull();

        organization.Display.ShouldBe("Nana Hospitals, Pune");
        organization.Identifier.System.ShouldBe("https://facility.ndhm.gov.in");
        organization.Identifier.Value.ShouldBe("IN2710000123");
    }
}
