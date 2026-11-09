using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>How a practitioner master is compared with ABDM's Healthcare Professionals Registry.</summary>
public sealed class HprCheckTests
{
    private static Practitioner Master(string name = "Dr. Asha Menon", string? council = "Tamil Nadu Medical Council", string? number = "TNMC/0012345") =>
        new() { DisplayName = name, RegistrationCouncil = council, RegistrationNumber = number, HprId = "71-1234-5678-9012" };

    private static HprRegistryRecord Registry(string name = "Asha Menon", string council = "Tamil Nadu Medical Council", string number = "TNMC12345", bool active = true) =>
        new("71-1234-5678-9012", name, council, number, active);

    [Theory]
    [InlineData("MENON ASHA")]
    [InlineData("Dr Asha  Menon")]
    [InlineData("Prof. Asha Ménon")]
    public void Names_match_without_titles_case_accents_or_order(string registered) =>
        PractitionerHprChecks.Compare(Master(), Registry(name: registered)).ShouldBeEmpty();

    [Fact]
    public void A_different_name_is_a_mismatch() =>
        PractitionerHprChecks.Compare(Master(), Registry(name: "Asha Nair")).ShouldBe(["name"]);

    [Theory]
    [InlineData("Medical Council of Tamil Nadu", true)]
    [InlineData("tamil nadu medical council", true)]
    [InlineData("TN Medical Council", false)]
    public void Councils_match_without_filler_words_but_abbreviations_are_not_guessed(string registered, bool matches) =>
        PractitionerHprChecks.Compare(Master(), Registry(council: registered)).Contains("council").ShouldBe(!matches);

    [Theory]
    [InlineData("TNMC 12345", true)]
    [InlineData("tnmc-0012345", true)]
    [InlineData("TNMC 12346", false)]
    public void Registration_numbers_match_without_punctuation_case_or_leading_zeros(string registered, bool matches) =>
        PractitionerHprChecks.Compare(Master(), Registry(number: registered)).Contains("registrationNumber").ShouldBe(!matches);

    [Fact]
    public void A_registration_the_registry_marks_inactive_is_a_mismatch() =>
        PractitionerHprChecks.Compare(Master(), Registry(active: false)).ShouldBe(["status"]);

    [Fact]
    public void A_master_without_a_council_or_number_does_not_match() =>
        PractitionerHprChecks.Compare(Master(council: null, number: null), Registry()).ShouldBe(["council", "registrationNumber"]);
}
