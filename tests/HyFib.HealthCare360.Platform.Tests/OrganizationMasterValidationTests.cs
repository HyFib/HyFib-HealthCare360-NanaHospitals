using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The organisation masters' requests are held to their columns and rules before they reach the database, so a value that
/// is too long is a field error for the administrator rather than a server error.
/// </summary>
public sealed class OrganizationMasterValidationTests
{
    [Theory]
    [InlineData("www.nanahospitals.in", true)]
    [InlineData("https://nanahospitals.in/care", true)]
    [InlineData("http://nanahospitals.in", true)]
    [InlineData("nanahospitals", false)]
    [InlineData("ftp://nanahospitals.in", false)]
    [InlineData("www.nana hospitals.in", false)]
    public void A_website_is_a_web_address(string value, bool valid) => MasterRules.IsWebAddress(value).ShouldBe(valid);

    [Fact]
    public void A_complete_facility_is_accepted() => new SaveFacilityRequestValidator().Validate(Facility()).IsValid.ShouldBeTrue();

    [Fact]
    public void Facility_details_are_held_to_their_columns()
    {
        var result = new SaveFacilityRequestValidator().Validate(Facility() with
        {
            LegalName = new string('a', 201),
            Address = new Address { City = new string('c', 101), PinCode = "12345" },
            Website = "nanahospitals",
            LetterheadNote = new string('n', 301),
            HfrId = new string('h', 65),
        });

        result.Errors.Select(e => e.PropertyName).Distinct().ShouldBe(
            ["LegalName", "Address.City", "Address.PinCode", "Website", "LetterheadNote", "HfrId"], ignoreOrder: true);
    }

    [Fact]
    public void A_department_keeps_its_order_and_token_prefix_short()
    {
        var result = new SaveDepartmentRequestValidator().Validate(new SaveDepartmentRequest(
            "GM", "General medicine", (DepartmentKind)42, null, null, new string('l', 129), null, "GMED1", 10_000, true, null));

        result.Errors.Select(e => e.PropertyName).Distinct().ShouldBe(["Kind", "Location", "TokenPrefix", "DisplayOrder"], ignoreOrder: true);
    }

    [Fact]
    public void A_doctor_needs_a_registration_number_and_sensible_fees_and_languages()
    {
        var result = new SavePractitionerRequestValidator().Validate(Doctor() with
        {
            RegistrationNumber = null,
            ConsultationFeeNew = 500.555m,
            ConsultationFeeFollowUp = 2_000_000m,
            Languages = [.. Enumerable.Range(1, 13).Select(i => $"Language {i}")],
            Bio = new string('b', 2001),
        });

        result.Errors.Select(e => e.PropertyName).Distinct().ShouldBe(
            ["RegistrationNumber", "ConsultationFeeNew", "ConsultationFeeFollowUp", "Languages", "Bio"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("TN")]
    [InlineData("Tamilnadu")]
    [InlineData("Pondicherry")]
    [InlineData("")]
    public void A_facilitys_state_may_be_written_as_people_write_it(string state) =>
        new SaveFacilityRequestValidator().Validate(Facility() with { Address = Facility().Address! with { State = state } }).IsValid.ShouldBeTrue();

    [Fact]
    public void A_facilitys_state_must_be_one_of_india_unless_it_is_abroad()
    {
        var result = new SaveFacilityRequestValidator().Validate(Facility() with { Address = Facility().Address! with { State = "Tamizh" } });

        result.Errors.Select(e => e.PropertyName).ShouldBe(["Address.State"]);
        new SaveFacilityRequestValidator().Validate(Facility() with { Address = new Address { State = "Bavaria", Country = "Germany" } })
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_resident_is_a_doctor_and_needs_a_registration_number() =>
        new SavePractitionerRequestValidator().Validate(Doctor() with { Kind = PractitionerKind.Resident, RegistrationNumber = null })
            .Errors.Select(e => e.PropertyName).ShouldBe(["RegistrationNumber"]);

    [Fact]
    public void A_nurse_needs_no_council_registration() =>
        new SavePractitionerRequestValidator().Validate(Doctor() with { Kind = PractitionerKind.Nurse, RegistrationNumber = null }).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("71-3016-8543-4357", true)]
    [InlineData("71 3016 8543 4357", true)]
    [InlineData("71301685434357", true)]
    [InlineData(null, true)] // a doctor not yet in the registry
    [InlineData("7130168543435", false)] // 13 digits
    [InlineData("asha.menon@hpr.abdm", false)] // the HPR address, not the id
    public void An_hpr_id_is_14_digits(string? hprId, bool valid)
    {
        var result = new SavePractitionerRequestValidator().Validate(Doctor() with { HprId = hprId });

        result.IsValid.ShouldBe(valid);
        result.Errors.Select(e => e.PropertyName).ShouldAllBe(p => p == "HprId");
    }

    [Fact]
    public void A_service_has_a_tidy_code_a_sac_code_and_a_tax_rate_within_gst()
    {
        var result = new SaveServiceItemRequestValidator().Validate(new SaveServiceItemRequest(
            "LAB CBC", "Complete blood count", new string('s', 65), ServiceCategory.Laboratory, null, 350m, 30m, "SAC9993", true, false, null, true, null));

        result.Errors.Select(e => e.PropertyName).Distinct().ShouldBe(["Code", "ShortName", "TaxRate", "SacCode"], ignoreOrder: true);
    }

    [Fact]
    public void A_service_is_named_in_languages_by_their_codes()
    {
        var named = Service() with { LocalNames = new Dictionary<string, string> { ["ta"] = "முழு இரத்த எண்ணிக்கை", ["hi"] = "पूर्ण रक्त गणना" } };
        new SaveServiceItemRequestValidator().Validate(named).IsValid.ShouldBeTrue();

        var result = new SaveServiceItemRequestValidator().Validate(Service() with
        {
            LocalNames = new Dictionary<string, string> { ["Tamil"] = "முழு இரத்த எண்ணிக்கை", ["hi"] = new string('ह', 201) },
        });

        result.Errors.Count.ShouldBe(2);
        result.Errors.ShouldAllBe(e => e.PropertyName.StartsWith("LocalNames[", StringComparison.Ordinal));
    }

    [Fact]
    public void A_service_like_the_seeded_ones_is_accepted() =>
        new SaveServiceItemRequestValidator().Validate(new SaveServiceItemRequest(
            "LAB-CBC", "Complete blood count", "CBC", ServiceCategory.Laboratory, null, 350m, 0m, "999312", true, false, null, true, 3))
            .IsValid.ShouldBeTrue();

    private static SaveServiceItemRequest Service() =>
        new("LAB-CBC", "Complete blood count", "CBC", ServiceCategory.Laboratory, null, 350m, 0m, "999312", true, false, null, true, 3);

    private static SaveFacilityRequest Facility() => new(
        "NH", "Nana Hospitals", "Nana Hospitals Private Limited", new Address { Line1 = "12 Anna Salai", City = "Chennai", State = "Tamil Nadu", PinCode = "600002" },
        "9876543210", "care@nanahospitals.in", "www.nanahospitals.in", "33ABCDE1234F1Z5", "ABCDE1234F", "TN/CEA/2024/123", "PEH-2024-1234",
        "IN3310000001", "NABH accredited hospital", null, true, null);

    private static SavePractitionerRequest Doctor() => new(
        "DR101", "Dr.", "Asha", "Menon", Gender.Female, "MBBS, MD (General Medicine)", "Tamil Nadu Medical Council", "TNMC 12345", null, null, null,
        PractitionerKind.Consultant, "9876543210", "asha.menon@nanahospitals.in", 500m, 300m, 7, ["English", "Tamil"], null, null, null, true, false,
        null, true, null);
}
