using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using Reached = HyFib.HealthCare360.Modules.Patients.Services.PortalPatientScope.Reached;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// What a portal account sees of the records its number reaches (<see cref="PortalPatientScope"/>): a record on its own
/// number once that record's number is verified, and until then it waits for the account to confirm it with the patient's
/// date of birth or UHID; a family member through a link as before; and the protected-category rule for a shared number
/// as before.
/// </summary>
public sealed class PortalScopeVerificationTests
{
    [Fact]
    public void An_unverified_record_on_the_accounts_number_is_not_shown_but_waits_to_be_confirmed()
    {
        var record = Own(verified: false);

        PortalPatientScope.Shown([record]).ShouldBeEmpty("a digit typed wrong at the desk would hand the record to a stranger");
        PortalPatientScope.Waiting([record]).ShouldBe([record.Id]);
    }

    [Fact]
    public void A_verified_record_on_the_number_is_shown_and_waits_for_nothing()
    {
        var record = Own(verified: true);

        PortalPatientScope.Shown([record]).ShouldBe([record.Id]);
        PortalPatientScope.Waiting([record]).ShouldBeEmpty();
    }

    [Fact]
    public void A_family_shares_a_number_and_each_record_is_shown_once_its_own_number_is_verified()
    {
        var mother = Own(verified: true);
        var son = Own(verified: false);

        PortalPatientScope.Shown([mother, son]).ShouldBe([mother.Id]);
        PortalPatientScope.Waiting([mother, son]).ShouldBe([son.Id]);
    }

    [Fact]
    public void A_family_member_reached_through_a_link_is_shown_as_before_whether_or_not_their_number_is_verified()
    {
        var relative = new Reached(Guid.CreateVersion7(), OwnNumber: false, Linked: true, Verified: false, SensitiveCategory.None);

        PortalPatientScope.Shown([relative]).ShouldBe([relative.Id]);
        PortalPatientScope.Waiting([relative]).ShouldBeEmpty("it is not the account's number to confirm");
    }

    [Fact]
    public void A_protected_record_on_a_shared_number_is_neither_shown_nor_offered_even_when_the_other_record_is_unverified()
    {
        var protectedRecord = Own(verified: true, SensitiveCategory.Hiv);
        var unverifiedSibling = Own(verified: false);

        // The number is shared whether or not the other record on it is verified: the phone may be the sibling's.
        PortalPatientScope.Shown([protectedRecord, unverifiedSibling]).ShouldBeEmpty();
        PortalPatientScope.Waiting([protectedRecord, unverifiedSibling]).ShouldBe([unverifiedSibling.Id]);

        var unverifiedProtected = Own(verified: false, SensitiveCategory.MentalHealth);
        PortalPatientScope.Waiting([unverifiedProtected, Own(verified: true)])
            .ShouldNotContain(unverifiedProtected.Id, "confirming it would show nothing: the hospital gives it in person");
    }

    [Fact]
    public void A_protected_record_on_the_patients_own_number_is_shown_only_once_confirmed()
    {
        var waiting = Own(verified: false, SensitiveCategory.Mtp);
        PortalPatientScope.Shown([waiting]).ShouldBeEmpty();
        PortalPatientScope.Waiting([waiting]).ShouldBe([waiting.Id]);

        var confirmed = waiting with { Verified = true };
        PortalPatientScope.Shown([confirmed]).ShouldBe([confirmed.Id]);
    }

    [Fact]
    public void A_sole_verified_patient_on_the_number_is_identified_by_the_sign_in()
    {
        var record = Own(verified: true);

        PortalPatientScope.Identifies([record], record.Id).ShouldBeTrue();
        PortalPatientScope.Identifies([record], Guid.CreateVersion7()).ShouldBeFalse();
    }

    [Fact]
    public void A_sole_patient_whose_number_is_not_verified_is_not_identified()
    {
        var record = Own(verified: false);

        PortalPatientScope.Identifies([record], record.Id).ShouldBeFalse();
    }

    [Fact]
    public void On_a_number_a_family_shares_no_patient_is_identified_even_when_every_record_is_verified()
    {
        var father = Own(verified: true);
        var son = Own(verified: true);

        PortalPatientScope.Identifies([father, son], father.Id).ShouldBeFalse();
        PortalPatientScope.Identifies([father, son], son.Id).ShouldBeFalse();
    }

    private static Reached Own(bool verified, SensitiveCategory categories = SensitiveCategory.None) =>
        new(Guid.CreateVersion7(), OwnNumber: true, Linked: false, verified, categories);
}
