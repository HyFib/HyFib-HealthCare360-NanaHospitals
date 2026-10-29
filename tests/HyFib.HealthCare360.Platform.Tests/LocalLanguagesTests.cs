using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A facility's local language, which its rate list is displayed in beside English (Clinical Establishments Rules 2012,
/// r.9(i)) and which the services' names are printed in: the hospital's choice, else its state's official language,
/// read from however the address writes the state, else its GSTIN's.
/// </summary>
public sealed class LocalLanguagesTests
{
    [Theory]
    [InlineData(null, "Tamil Nadu", null, null, "ta")]
    [InlineData(null, "TN", null, null, "ta")]
    [InlineData(null, "Tamilnadu", null, null, "ta")]
    [InlineData(null, "Uttar Pradesh", null, null, "hi")]
    [InlineData(null, "Karnataka", null, null, "kn")]
    [InlineData(null, "Kerala", null, null, "ml")]
    [InlineData(null, "Puducherry", "605001", null, "ta")]
    [InlineData(null, "Puducherry", "673310", null, "ml")] // Mahe
    [InlineData(null, "Puducherry", "533464", null, "te")] // Yanam
    [InlineData(null, "Meghalaya", null, null, null)] // English is its official language
    [InlineData(null, "Jammu and Kashmir", null, null, null)] // five, none first
    [InlineData(null, "Kerala", null, "33ABCDE1234F1Z5", "ml")] // the address decides
    [InlineData(null, null, null, "33ABCDE1234F1Z5", "ta")] // no state: the GSTIN's
    [InlineData(null, "Tamizh", null, "09ABCDE1234F1Z5", "hi")] // no state it knows: the GSTIN's
    [InlineData("kn", "Tamil Nadu", null, null, "kn")] // the hospital's choice
    [InlineData("Kannada", "Tamil Nadu", null, null, "kn")]
    [InlineData(null, null, null, null, null)]
    public void A_facilitys_language_is_the_hospitals_choice_else_its_states(string? setting, string? state, string? pin, string? gstin, string? language) =>
        (LocalLanguages.OfFacility(setting, new Address { State = state, PinCode = pin }, gstin).Language?.Code).ShouldBe(language);

    [Fact]
    public void A_state_it_does_not_know_is_told_when_nothing_else_gives_the_language()
    {
        var language = LocalLanguages.OfFacility(null, new Address { State = "Tamizh Naadu" }, null);

        (language.Language, language.UnknownState, language.UnknownSetting).ShouldBe((null, "Tamizh Naadu", null));
    }

    [Fact]
    public void A_choice_naming_no_language_is_told_and_the_states_is_used_meanwhile()
    {
        var language = LocalLanguages.OfFacility("Tamizh", new Address { State = "Tamil Nadu" }, null);

        (language.Language?.Code, language.UnknownSetting).ShouldBe(("ta", "Tamizh"));
    }

    [Fact]
    public void Every_states_language_is_one_a_service_can_be_named_in() =>
        Gstin.States.Keys.Select(code => LocalLanguages.OfState(code)).OfType<IndianLanguage>().ShouldAllBe(l => LocalLanguages.IsCode(l.Code));

    [Theory]
    [InlineData("ta", "ta")]
    [InlineData("TA", "ta")]
    [InlineData("ta-IN", "ta")]
    [InlineData(" Tamil ", "ta")]
    [InlineData("kok", "kok")]
    [InlineData("tamizh", null)]
    [InlineData("", null)]
    public void A_language_is_found_by_its_code_or_english_name(string text, string? code) => (LocalLanguages.Find(text)?.Code).ShouldBe(code);
}
