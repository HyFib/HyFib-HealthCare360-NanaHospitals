using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// The patient's record (maker-checker, slice 7): when the desk still corrects an identity at once, and how a change is
/// put in words for the one who approves it.
/// </summary>
public sealed class PatientRecordChangeTests
{
    private static readonly DateTimeOffset Entered = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Desk = Guid.CreateVersion7();

    [Fact]
    public void The_desk_that_registered_the_patient_corrects_the_identity_at_once_for_a_day()
    {
        IdentityCorrection.OpenUntil(Entered, Desk, Desk, seen: false, Entered.AddHours(23)).ShouldBe(Entered.AddHours(24));
        IdentityCorrection.OpenUntil(Entered, Desk, Desk, seen: false, Entered.AddHours(24)).ShouldBeNull("a day later it waits");
    }

    [Fact]
    public void Anyone_else_or_after_the_patient_has_been_seen_asks()
    {
        IdentityCorrection.OpenUntil(Entered, Desk, Guid.CreateVersion7(), seen: false, Entered.AddHours(1)).ShouldBeNull("another person");
        IdentityCorrection.OpenUntil(Entered, Desk, Desk, seen: true, Entered.AddHours(1)).ShouldBeNull("the patient has been seen");
        IdentityCorrection.OpenUntil(Entered, null, null, seen: false, Entered.AddHours(1)).ShouldBeNull("nobody is signed in");
        IdentityCorrection.OpenUntil(Entered, null, Desk, seen: false, Entered.AddHours(1)).ShouldBeNull("entered by no known desk (an import)");
    }

    [Fact]
    public void An_identity_change_is_shown_field_by_field_and_a_restricted_records_names_by_their_initials()
    {
        var before = new PatientIdentity("Mr.", "Ravi", null, "Kumar", Gender.Male, new DateOnly(1980, 3, 12), false, null, null, "1234");
        var after = before with { FirstName = "Ravee", Gender = Gender.Other, DateOfBirth = new DateOnly(1981, 1, 1), IsDateOfBirthEstimated = true, AadhaarLast4 = null };

        PatientIdentity.Changes(before, after, restricted: false).ShouldBe(new (string, string)[]
        {
            ("Name", "Mr. Ravi Kumar → Mr. Ravee Kumar"),
            ("Sex", "Male → Other"),
            ("Date of birth", "12 Mar 1980 → 1 Jan 1981 (estimated from the age)"),
            ("Aadhaar", "ending 1234 → none"),
        });
        PatientIdentity.Describe(before, after, restricted: true).ShouldStartWith("name R. K. (restricted) → R. K. (restricted);");
        PatientIdentity.Describe(before, before, restricted: false).ShouldBe("unchanged");
    }
}
