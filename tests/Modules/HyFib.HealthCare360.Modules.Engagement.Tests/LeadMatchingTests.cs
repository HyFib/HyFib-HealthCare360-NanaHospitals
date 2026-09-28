using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

/// <summary>Families share mobile numbers, so a booking converts a lead only when the names also agree.</summary>
public sealed class LeadMatchingTests
{
    [Theory]
    [InlineData("Ravi Kumar", "Ravi Kumar")]
    [InlineData("ravi", "Ravi Kumar S")]
    [InlineData("Mrs. Lakshmi", "Lakshmi Narayanan")]
    [InlineData("R. Kumar", "Ravi Kumar")]
    [InlineData("Dr Anand", "Anand Raj")]
    public void Same_first_name_or_initial_with_a_shared_surname_is_the_same_person(string lead, string patient) =>
        LeadMatching.IsSamePerson(lead, patient).ShouldBeTrue();

    [Theory]
    [InlineData("Ravi Kumar", "Lakshmi Kumar")]
    [InlineData("R. Kumar", "Rani Devi")]
    [InlineData("R", "Ravi")]
    [InlineData("", "Ravi")]
    [InlineData("Ravi", null)]
    public void A_family_member_sharing_the_number_is_not_matched(string? lead, string? patient) =>
        LeadMatching.IsSamePerson(lead, patient).ShouldBeFalse();
}
