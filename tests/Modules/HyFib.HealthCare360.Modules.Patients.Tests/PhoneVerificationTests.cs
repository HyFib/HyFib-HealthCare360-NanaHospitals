using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// A patient's mobile number is verified only while the record still has the number that was verified
/// (<see cref="Patient.IsPhoneVerified"/>): a change of the number, by whatever route, leaves the new one unverified until it
/// is verified in turn. Documents go only to a verified number (<see cref="VerifiedNumbers"/>).
/// </summary>
public sealed class PhoneVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);
    private const string Number = "+919876543210";

    [Fact]
    public void A_number_nobody_has_verified_is_not_verified()
    {
        var patient = new Patient { Phone = Number };

        patient.IsPhoneVerified.ShouldBeFalse();
        Verified(patient).ShouldBeFalse();
    }

    [Fact]
    public void Verifying_marks_the_number_the_record_has_now_and_when()
    {
        var patient = new Patient { Phone = Number };

        patient.MarkPhoneVerified(Now);

        (patient.PhoneVerifiedAt, patient.PhoneVerifiedNumber, patient.IsPhoneVerified).ShouldBe((Now, Number, true));
        Verified(patient).ShouldBeTrue();
    }

    [Fact]
    public void A_change_of_the_number_leaves_the_new_one_unverified_until_it_is_verified_in_turn()
    {
        var patient = new Patient { Phone = Number };
        patient.MarkPhoneVerified(Now);

        patient.Phone = "+919876543211";

        patient.IsPhoneVerified.ShouldBeFalse("the digit typed differently is not the number the patient read a code from");
        Verified(patient).ShouldBeFalse();

        patient.MarkPhoneVerified(Now.AddMinutes(5));
        (patient.PhoneVerifiedNumber, patient.PhoneVerifiedAt, patient.IsPhoneVerified).ShouldBe(("+919876543211", Now.AddMinutes(5), true));
    }

    [Fact]
    public void Removing_the_number_leaves_nothing_verified_and_a_record_without_one_has_nothing_to_verify()
    {
        var patient = new Patient { Phone = Number };
        patient.MarkPhoneVerified(Now);

        patient.Phone = null;
        patient.IsPhoneVerified.ShouldBeFalse();
        Verified(patient).ShouldBeFalse();

        var none = new Patient();
        none.MarkPhoneVerified(Now);
        (none.PhoneVerifiedAt, none.PhoneVerifiedNumber, none.IsPhoneVerified).ShouldBe((null, null, false));
    }

    [Fact]
    public void A_message_to_an_unverified_number_carries_no_document()
    {
        var report = new NotificationDocument("diagnostics.lab-report", Guid.CreateVersion7());

        VerifiedNumbers.ForNumber(report, numberVerified: true).ShouldBe(report);
        VerifiedNumbers.ForNumber(report, numberVerified: false).ShouldBeNull();
        VerifiedNumbers.ForNumber(null, numberVerified: true).ShouldBeNull();
    }

    [Fact]
    public void A_code_that_verified_a_number_says_so_when_it_is_entered_again()
    {
        var verification = new PatientPhoneChange
        {
            Purpose = PhoneCodePurpose.Verify,
            Phone = Number,
            PreviousPhone = Number,
            ExpiresAt = Now + PatientPhoneChange.Lifetime,
            Status = PatientPhoneChangeStatus.Pending,
        };
        verification.CodeHash = PatientPhoneChange.HashOf(verification.Id, "482913");

        verification.Confirm(PatientPhoneChange.HashOf(verification.Id, "482913"), Guid.CreateVersion7(), Now.AddMinutes(1)).IsSuccess.ShouldBeTrue();
        var again = verification.Confirm(PatientPhoneChange.HashOf(verification.Id, "482913"), null, Now.AddMinutes(2));

        again.Error!.Code.ShouldBe(PatientPhoneChange.ExpiredCode);
        again.Error.Message.ShouldBe("This number is verified already.");
    }

    /// <summary>The rule as a query runs it (<see cref="VerifiedNumbers.Verified"/>), which must agree with the record's.</summary>
    private static bool Verified(Patient patient) => VerifiedNumbers.Verified.Compile()(patient);
}
