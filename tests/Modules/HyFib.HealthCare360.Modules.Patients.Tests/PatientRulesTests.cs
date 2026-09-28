using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

public sealed class PatientRulesTests
{
    [Theory]
    [InlineData("Ramesh Kumar")]
    [InlineData("D'Souza")]
    [InlineData("Mary-Ann")]
    [InlineData("K. Lakshmi")]
    [InlineData("रमेश कुमार")]
    [InlineData("லட்சுமி")]
    [InlineData("ಶ್ರೀನಿವಾಸ")]
    public void Accepts_names_in_latin_and_indian_scripts(string name) => PatientRules.ValidName(name).ShouldBeTrue();

    [Theory]
    [InlineData("Ramesh1")]
    [InlineData("<script>")]
    [InlineData("Ravi_Kumar")]
    public void Rejects_digits_and_symbols(string name) => PatientRules.ValidName(name).ShouldBeFalse();

    [Theory]
    [InlineData("2345 6789 0124")]
    [InlineData(null)]
    [InlineData("")]
    public void Accepts_valid_or_missing_aadhaar(string? aadhaar) => PatientRules.ValidAadhaar(aadhaar).ShouldBeTrue();

    [Theory]
    [InlineData("2345 6789 0123")] // wrong Verhoeff check digit
    [InlineData("1234 5678 9012")] // cannot start with 0 or 1
    [InlineData("2345 6789 012")] // 11 digits
    public void Rejects_invalid_aadhaar(string aadhaar) => PatientRules.ValidAadhaar(aadhaar).ShouldBeFalse();

    [Fact]
    public void Estimates_date_of_birth_from_age()
    {
        var today = new DateOnly(2026, 9, 26);

        var dob = PatientRules.ResolveDateOfBirth(null, 40, 6, today, out var estimated);

        estimated.ShouldBeTrue();
        dob.ShouldNotBeNull();
        Age.InYears(dob.Value, today).ShouldBe(40);
    }

    [Fact]
    public void Prefers_the_given_date_of_birth()
    {
        var given = new DateOnly(1990, 1, 15);

        PatientRules.ResolveDateOfBirth(given, 10, null, new DateOnly(2026, 9, 26), out var estimated).ShouldBe(given);
        estimated.ShouldBeFalse();
    }
}
