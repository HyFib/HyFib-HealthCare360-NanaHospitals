using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

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
    [InlineData("T74.2", SensitiveCategory.SexualViolence)]
    [InlineData("T74.22", SensitiveCategory.SexualViolence)]
    [InlineData("T76.21", SensitiveCategory.SexualViolence)]
    [InlineData("Y05.0", SensitiveCategory.SexualViolence)]
    [InlineData("Z04.4", SensitiveCategory.SexualViolence)]
    [InlineData("Z044", SensitiveCategory.SexualViolence)]
    [InlineData("T74.1", SensitiveCategory.None)]
    [InlineData("T74", SensitiveCategory.None)]
    [InlineData("Z04.1", SensitiveCategory.None)]
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
    public void Categories_are_described_in_plain_words()
    {
        SensitiveCategories.Describe(SensitiveCategory.Hiv | SensitiveCategory.Mtp).ShouldBe("HIV, termination of pregnancy");
        SensitiveCategories.Describe(SensitiveCategory.SexualViolence).ShouldBe("sexual violence");
    }

    [Fact]
    public void Every_category_has_the_permission_that_opens_it()
    {
        // A category with no permission would be held by everyone: the record would not be closed to anyone.
        var categories = Enum.GetValues<SensitiveCategory>().Where(c => c != SensitiveCategory.None).ToList();
        categories.Aggregate(SensitiveCategory.None, (all, c) => all | c).ShouldBe(SensitiveCategories.All);
        PatientAccessGuard.CategoryPermissions.Select(p => p.Category).ShouldBe(categories, ignoreOrder: true);
        PatientAccessGuard.CategoryPermissions.Select(p => p.Permission).ShouldBeUnique();
    }
}
