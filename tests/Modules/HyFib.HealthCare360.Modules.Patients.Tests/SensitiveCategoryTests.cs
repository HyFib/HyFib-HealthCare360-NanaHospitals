using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>Which diagnoses make a record confidential (R-4), and which do not.</summary>
public sealed class SensitiveCategoryTests
{
    [Theory]
    [InlineData("B20", SensitiveCategory.Hiv)]
    [InlineData("B24", SensitiveCategory.Hiv)]
    [InlineData(" z21 ", SensitiveCategory.Hiv)]
    [InlineData("F20.9", SensitiveCategory.MentalHealth)]
    [InlineData("F32.1", SensitiveCategory.MentalHealth)]
    [InlineData("F69", SensitiveCategory.MentalHealth)]
    [InlineData("O04.9", SensitiveCategory.Mtp)]
    [InlineData("F03", SensitiveCategory.None)]
    [InlineData("F10.2", SensitiveCategory.None)]
    [InlineData("F84.0", SensitiveCategory.None)]
    [InlineData("B25", SensitiveCategory.None)]
    [InlineData("O03.9", SensitiveCategory.None)]
    [InlineData("R50.9", SensitiveCategory.None)]
    [InlineData("F", SensitiveCategory.None)]
    [InlineData(null, SensitiveCategory.None)]
    public void A_diagnosis_puts_the_record_in_its_category(string? code, SensitiveCategory expected) =>
        SensitiveCategories.FromIcd(code).ShouldBe(expected);

    [Fact]
    public void Categories_are_described_in_plain_words() =>
        SensitiveCategories.Describe(SensitiveCategory.Hiv | SensitiveCategory.Mtp).ShouldBe("HIV, termination of pregnancy");
}
