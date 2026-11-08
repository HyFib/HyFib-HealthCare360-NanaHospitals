using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>
/// The recovery record's own rules: the chart is kept from the arrival, the anaesthetist hands over once, and the patient
/// leaves for the ward on fresh observations scoring 9 or more, or on an anaesthetist's override with a reason; for ICU or
/// HDU at any score; never before the surgery is completed in the theatre. Once discharged the record is closed.
/// </summary>
public sealed class RecoveryRecordTests
{
    private static readonly DateTimeOffset Arrived = TestData.Now;

    [Fact]
    public void Fresh_observations_scoring_nine_or_more_send_the_patient_to_the_ward()
    {
        var record = Received();
        record.RecordHandover(TestData.Staff(Arrived.AddMinutes(2), "Dr. Ravi Menon"), confirmedAtScreen: false, null).IsSuccess.ShouldBeTrue();
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(30))).IsSuccess.ShouldBeTrue();

        var nurse = TestData.Staff(Arrived.AddMinutes(40));
        record.Discharge(RecoveryDestination.Ward, surgeryCompleted: true, handoverRequired: true, nurse).IsSuccess.ShouldBeTrue();

        (record.DischargedAt, record.Destination, record.DischargeScore, record.DischargedByName).ShouldBe((nurse.At, RecoveryDestination.Ward, 10, nurse.Name));
        record.OverrideReason.ShouldBeNull();
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(45))).Error!.Code.ShouldBe("recovery.discharged");
    }

    [Fact]
    public void Below_nine_the_ward_is_refused_and_says_why()
    {
        var record = Received(handedOver: true);
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(30)) with { Consciousness = RecoveryConsciousness.Voice, SpO2 = 95, OnOxygen = true })
            .IsSuccess.ShouldBeTrue();

        var refused = record.Discharge(RecoveryDestination.Ward, true, true, TestData.Staff(Arrived.AddMinutes(35)));

        refused.Error!.Code.ShouldBe("recovery.score_too_low");
        refused.Error.Message.ShouldStartWith("The modified Aldrete score is 8 of 10.");
        record.IsDischarged.ShouldBeFalse();
    }

    [Fact]
    public void Observations_more_than_fifteen_minutes_old_do_not_send_the_patient_to_the_ward()
    {
        var record = Received(handedOver: true);
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(10))).IsSuccess.ShouldBeTrue();

        record.Discharge(RecoveryDestination.Ward, true, true, TestData.Staff(Arrived.AddMinutes(26))).Error!.Message
            .ShouldStartWith("The last observations are more than 15 minutes old");
        record.Discharge(RecoveryDestination.Ward, true, true, TestData.Staff(Arrived.AddMinutes(25))).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_score_that_cannot_be_given_names_what_to_record()
    {
        var record = Received(handedOver: true, preAnaesthesiaSystolic: null);
        record.Discharge(RecoveryDestination.Ward, true, true, TestData.Staff(Arrived.AddMinutes(5))).Error!.Message.ShouldStartWith("No observations are recorded yet.");

        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(10))).IsSuccess.ShouldBeTrue();
        record.Discharge(RecoveryDestination.Ward, true, true, TestData.Staff(Arrived.AddMinutes(12))).Error!.Message
            .ShouldStartWith("The score cannot be given yet: record the pre-anaesthetic blood pressure.");
    }

    [Theory]
    [InlineData(RecoveryDestination.Icu)]
    [InlineData(RecoveryDestination.Hdu)]
    public void Intensive_care_takes_the_patient_at_any_score(RecoveryDestination destination)
    {
        var record = Received(handedOver: true);
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(5)) with { Consciousness = RecoveryConsciousness.Pain, SpO2 = 89, OnOxygen = true })
            .IsSuccess.ShouldBeTrue();

        record.Discharge(destination, true, true, TestData.Staff(Arrived.AddMinutes(8))).IsSuccess.ShouldBeTrue();
        (record.Destination, record.DischargeScore).ShouldBe((destination, 6));
    }

    [Fact]
    public void The_anaesthetist_overrides_the_score_with_a_reason()
    {
        var record = Received(handedOver: true);
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(30)) with { LimbsMoving = 0 }).IsSuccess.ShouldBeTrue();
        var anaesthetist = TestData.Staff(Arrived.AddMinutes(35), "Dr. Ravi Menon");

        record.Discharge(RecoveryDestination.Ward, true, true, anaesthetist, anaesthetist, " ").Error!.Code.ShouldBe("validation");
        record.Discharge(RecoveryDestination.Ward, true, true, anaesthetist, anaesthetist, "Spinal block still wearing off; legs expected back on the ward")
            .IsSuccess.ShouldBeTrue();

        (record.DischargeScore, record.OverrideByName, record.OverrideReason).ShouldBe((8, "Dr. Ravi Menon", "Spinal block still wearing off; legs expected back on the ward"));
    }

    [Fact]
    public void No_override_is_recorded_where_none_is_needed()
    {
        var record = Received(handedOver: true);
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(30))).IsSuccess.ShouldBeTrue();
        var anaesthetist = TestData.Staff(Arrived.AddMinutes(35), "Dr. Ravi Menon");

        record.Discharge(RecoveryDestination.Ward, true, true, anaesthetist, anaesthetist, "Not needed").Error!.Code.ShouldBe("recovery.override_not_needed");
        record.Discharge(RecoveryDestination.Icu, true, true, anaesthetist, anaesthetist, "Not needed").Error!.Code.ShouldBe("recovery.override_not_needed");
    }

    [Fact]
    public void The_patient_does_not_leave_recovery_before_the_surgery_is_completed_or_without_the_handover()
    {
        var record = Received();
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(30))).IsSuccess.ShouldBeTrue();
        var nurse = TestData.Staff(Arrived.AddMinutes(35));

        record.Discharge(RecoveryDestination.Ward, surgeryCompleted: false, handoverRequired: false, nurse).Error!.Code.ShouldBe("recovery.surgery_not_completed");
        record.Discharge(RecoveryDestination.Icu, surgeryCompleted: true, handoverRequired: true, nurse).Error!.Code.ShouldBe("recovery.handover_missing");

        // Under local anaesthesia the surgeon gave it: there is no anaesthetist to hand over.
        record.Discharge(RecoveryDestination.Ward, surgeryCompleted: true, handoverRequired: false, nurse).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void The_handover_is_recorded_once_and_observations_follow_the_arrival()
    {
        var record = Received(handedOver: true);
        record.RecordHandover(TestData.Staff(Arrived.AddMinutes(3), "Dr. Asha Pillai"), true, null).Error!.Code.ShouldBe("recovery.handover_recorded");
        record.HandedOverByName.ShouldBe("Dr. Ravi Menon");

        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(-1))).Error!.Code.ShouldBe("validation");
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(15))).IsSuccess.ShouldBeTrue();
        record.AddObservation(AldreteScoreTests.Observation(Arrived.AddMinutes(5)) with { PainScore = 7 }).IsSuccess.ShouldBeTrue();
        record.Observations.Select(o => o.At).ShouldBe([Arrived.AddMinutes(5), Arrived.AddMinutes(15)]);
        record.Latest!.PainScore.ShouldBe(3, "the latest row is the latest in time, not the last written");
    }

    [Fact]
    public void The_booking_is_in_recovery_from_the_sign_out_until_discharged_from_it()
    {
        var booking = TestData.CountsTallied(TestData.InProgress());
        booking.InRecovery.ShouldBeFalse();

        booking.ConfirmSafety(SafetyPhase.SignOut, TestData.AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignOut]), TestData.Staff(Arrived.AddMinutes(140)))
            .IsSuccess.ShouldBeTrue();
        booking.InRecovery.ShouldBeTrue();
        booking.Complete(Arrived.AddMinutes(150)).IsSuccess.ShouldBeTrue();
        booking.InRecovery.ShouldBeTrue("the patient left the theatre for the recovery room");

        booking.DischargeFromRecovery(Arrived.AddMinutes(200), RecoveryDestination.Ward);
        booking.InRecovery.ShouldBeFalse();
    }

    private static RecoveryRecord Received(bool handedOver = false, int? preAnaesthesiaSystolic = 124)
    {
        var record = new RecoveryRecord
        {
            PatientId = Guid.CreateVersion7(),
            SurgeryBookingId = Guid.CreateVersion7(),
            ReceivedAt = Arrived,
            ReceivedByName = "Sr. Leela Thomas",
            Airway = AirwayDevice.None,
            Oxygen = OxygenDelivery.FaceMask,
            PreAnaesthesiaSystolic = preAnaesthesiaSystolic,
        };
        if (handedOver)
        {
            record.RecordHandover(TestData.Staff(Arrived.AddMinutes(2), "Dr. Ravi Menon"), confirmedAtScreen: false, "Uneventful GA").IsSuccess.ShouldBeTrue();
        }

        return record;
    }
}
