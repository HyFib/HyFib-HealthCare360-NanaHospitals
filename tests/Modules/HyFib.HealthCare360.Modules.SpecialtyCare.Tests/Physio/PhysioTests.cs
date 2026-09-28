using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Physio;

public sealed class PhysioTests
{
    [Fact]
    public void Each_modality_with_its_own_tariff_is_billed_separately() =>
        PhysioTariff.ChargeCodesFor([PhysioModality.IFT, PhysioModality.UST, PhysioModality.Exercises])
            .ShouldBe([PhysioServiceCodes.Ift, PhysioServiceCodes.Ust, PhysioServiceCodes.Exercise], ignoreOrder: true);

    [Fact]
    public void Modalities_without_a_tariff_add_one_general_session_charge() =>
        PhysioTariff.ChargeCodesFor([PhysioModality.TENS, PhysioModality.HotPack, PhysioModality.Traction])
            .ShouldBe([PhysioServiceCodes.Tens, PhysioServiceCodes.Session], ignoreOrder: true);

    [Fact]
    public void Duplicate_modalities_are_billed_once() =>
        PhysioTariff.ChargeCodesFor([PhysioModality.IFT, PhysioModality.IFT]).ShouldBe([PhysioServiceCodes.Ift]);

    [Fact]
    public void Sessions_cannot_exceed_the_treatment_plan()
    {
        var plan = new TreatmentPlan { SessionsPlanned = 10 };

        plan.EnsureCanSchedule(8, 2).IsSuccess.ShouldBeTrue();
        plan.EnsureCanSchedule(8, 3).Error!.Code.ShouldBe("physio.plan_exceeded");
        plan.EnsureCanSchedule(0, 0).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Treatment_cannot_be_planned_before_assessment()
    {
        var referral = new PhysioReferral { Status = PhysioReferralStatus.New };

        referral.StartTreatment().Error!.Code.ShouldBe("physio.assessment_required");

        referral.MarkAssessed();
        referral.StartTreatment().IsSuccess.ShouldBeTrue();
        referral.Status.ShouldBe(PhysioReferralStatus.InTreatment);
    }

    [Fact]
    public void Referral_with_treatment_given_must_be_completed_not_cancelled()
    {
        var referral = new PhysioReferral { Status = PhysioReferralStatus.InTreatment };

        referral.Cancel("Order withdrawn", hasAttendedSessions: true, TestData.Now).Error!.Code.ShouldBe("physio.has_sessions");
        referral.Complete("Pain-free, full range of motion", TestData.Now).IsSuccess.ShouldBeTrue();
        referral.IsOpen.ShouldBeFalse();
        referral.EnsureOpen().IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Untreated_referral_can_be_cancelled()
    {
        var referral = new PhysioReferral { Status = PhysioReferralStatus.New };

        referral.Cancel("Patient discharged", hasAttendedSessions: false, TestData.Now).IsSuccess.ShouldBeTrue();

        referral.Status.ShouldBe(PhysioReferralStatus.Cancelled);
        referral.ClosedAt.ShouldBe(TestData.Now);
    }

    [Fact]
    public void Attendance_needs_modalities_and_a_progress_note()
    {
        var session = new PhysioSession { Status = PhysioSessionStatus.Scheduled };

        session.Attend([], "Improving", null, null, null, TestData.Staff()).Error!.Fields!.ShouldContainKey("modalities");
        session.Attend([PhysioModality.IFT], " ", null, null, null, TestData.Staff()).Error!.Fields!.ShouldContainKey("progressNote");
    }

    [Fact]
    public void Attended_session_records_treatment_pain_and_therapist()
    {
        var session = new PhysioSession { Status = PhysioSessionStatus.Scheduled };
        var therapist = TestData.Staff(name: "Mr. Arun (PT)");

        session.Attend([PhysioModality.IFT, PhysioModality.Exercises], "Knee flexion 90 to 110 degrees", 6, 3, 40, therapist).IsSuccess.ShouldBeTrue();

        session.Status.ShouldBe(PhysioSessionStatus.Attended);
        session.PainBefore.ShouldBe(6);
        session.PainAfter.ShouldBe(3);
        session.TherapistName.ShouldBe("Mr. Arun (PT)");
        session.Attend([PhysioModality.IFT], "Again", null, null, null, therapist).Error!.Code.ShouldBe("physio.session_closed");
    }

    [Fact]
    public void Missed_sessions_count_against_the_plan_but_cancelled_ones_do_not()
    {
        var missed = new PhysioSession { Status = PhysioSessionStatus.Scheduled };
        missed.Close(PhysioSessionStatus.Missed, "Did not come").IsSuccess.ShouldBeTrue();
        missed.CountsAgainstPlan.ShouldBeTrue();

        var cancelled = new PhysioSession { Status = PhysioSessionStatus.Scheduled };
        cancelled.Close(PhysioSessionStatus.Cancelled, "Therapist on leave").IsSuccess.ShouldBeTrue();
        cancelled.CountsAgainstPlan.ShouldBeFalse();

        new PhysioSession().Close(PhysioSessionStatus.Attended, "x").IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Only_scheduled_sessions_can_be_moved()
    {
        var session = new PhysioSession { Status = PhysioSessionStatus.Scheduled };
        session.Reschedule(TestData.Now.AddDays(1)).IsSuccess.ShouldBeTrue();
        session.ScheduledAt.ShouldBe(TestData.Now.AddDays(1));

        session.Close(PhysioSessionStatus.Cancelled, "Holiday").IsSuccess.ShouldBeTrue();
        session.Reschedule(TestData.Now.AddDays(2)).IsFailure.ShouldBeTrue();
    }
}
