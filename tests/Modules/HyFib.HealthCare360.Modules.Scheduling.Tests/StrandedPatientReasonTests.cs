using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

/// <summary>Who the desk is asked to call when a doctor's leave strands a booking: the patients a message cannot reach.</summary>
public sealed class StrandedPatientReasonTests
{
    [Fact]
    public void A_patient_with_a_mobile_number_that_is_not_hidden_is_to_be_told_by_message() =>
        StrandedPatientNotifier.NotToldReason(alreadyTold: false, hasContact: true, hasPhone: true, hiddenFromNumber: false).ShouldBeNull();

    [Fact]
    public void A_patient_without_a_mobile_number_is_to_be_called()
    {
        var reason = StrandedPatientNotifier.NotToldReason(alreadyTold: false, hasContact: true, hasPhone: false, hiddenFromNumber: false);

        reason.ShouldNotBeNull();
        reason.ShouldContain("No mobile number");
    }

    [Fact]
    public void A_record_hidden_from_its_number_is_to_be_called_with_a_reason_that_does_not_name_the_category()
    {
        var reason = StrandedPatientNotifier.NotToldReason(alreadyTold: false, hasContact: true, hasPhone: true, hiddenFromNumber: true);

        reason.ShouldNotBeNull();
        reason.ShouldNotContain("protected", Case.Insensitive);
        reason.ShouldNotContain("sensitive", Case.Insensitive);
        reason.ShouldNotContain("shared", Case.Insensitive);
    }

    [Fact]
    public void A_patient_already_told_has_no_reason() =>
        StrandedPatientNotifier.NotToldReason(alreadyTold: true, hasContact: true, hasPhone: true, hiddenFromNumber: true).ShouldBeNull();
}
