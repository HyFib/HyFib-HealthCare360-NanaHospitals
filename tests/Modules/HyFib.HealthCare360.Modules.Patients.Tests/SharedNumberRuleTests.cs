using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// Who a portal account is shown (<see cref="SharedNumbers.ShownToAccount"/>): a record in a protected category only on
/// the patient's own number, and only while no other patient has that number. Every other record as before.
/// </summary>
public sealed class SharedNumberRuleTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void A_record_in_no_protected_category_is_shown_however_it_is_reached(bool ownNumber, bool numberShared, bool shown) =>
        SharedNumbers.ShownToAccount(ownNumber, numberShared, SensitiveCategory.None).ShouldBe(shown);

    [Theory]
    [InlineData(SensitiveCategory.Hiv)]
    [InlineData(SensitiveCategory.MentalHealth)]
    [InlineData(SensitiveCategory.Mtp)]
    [InlineData(SensitiveCategory.SexualViolence)]
    [InlineData(SensitiveCategory.Hiv | SensitiveCategory.MentalHealth)]
    public void A_protected_record_is_shown_only_on_the_patients_own_number_while_no_one_else_has_it(SensitiveCategory category)
    {
        SharedNumbers.ShownToAccount(ownNumber: true, numberShared: false, category).ShouldBeTrue("the number is the patient's alone");
        SharedNumbers.ShownToAccount(ownNumber: true, numberShared: true, category).ShouldBeFalse("another patient has the number");
        SharedNumbers.ShownToAccount(ownNumber: false, numberShared: false, category).ShouldBeFalse("a family member's number");
        SharedNumbers.ShownToAccount(ownNumber: false, numberShared: true, category).ShouldBeFalse("a family member's shared number");
    }
}
