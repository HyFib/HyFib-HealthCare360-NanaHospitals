using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>WHO Surgical Safety Checklist gating (OT-03): the safety pauses cannot be skipped or reordered.</summary>
public sealed class SurgicalSafetyChecklistTests
{
    private static IReadOnlyList<ChecklistInput> Phase(SafetyPhase phase) => TestData.AllDone(SurgicalSafetyChecklist.Phases[phase]);

    [Fact]
    public void Surgery_cannot_start_before_sign_in_and_time_out()
    {
        var booking = TestData.ReadyForSignIn();

        var result = booking.Start(TestData.Now);

        result.Error!.Code.ShouldBe("surgery.checklist_incomplete");
        booking.Status.ShouldBe(SurgeryStatus.InPreOp);
    }

    [Fact]
    public void Surgery_cannot_start_after_sign_in_alone()
    {
        var booking = TestData.ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff()).IsSuccess.ShouldBeTrue();

        booking.Start(TestData.Now).Error!.Code.ShouldBe("surgery.checklist_incomplete");
    }

    [Fact]
    public void Surgery_starts_once_sign_in_and_time_out_are_complete()
    {
        var booking = TestData.InProgress();

        booking.Status.ShouldBe(SurgeryStatus.InProgress);
        booking.StartedAt.ShouldBe(TestData.Now.AddMinutes(80));
        booking.InRoomAt.ShouldBe(TestData.Now.AddMinutes(60), "the sign-in marks wheels-in");
    }

    [Fact]
    public void Surgery_cannot_complete_before_sign_out()
    {
        var booking = TestData.InProgress();

        booking.Complete(TestData.Now.AddHours(3)).Error!.Code.ShouldBe("surgery.signout_incomplete");
        booking.Status.ShouldBe(SurgeryStatus.InProgress);
    }

    [Fact]
    public void Surgery_completes_after_sign_out()
    {
        var booking = TestData.InProgress();
        booking.ConfirmSafety(SafetyPhase.SignOut, Phase(SafetyPhase.SignOut), TestData.Staff(TestData.Now.AddHours(3))).IsSuccess.ShouldBeTrue();

        booking.Complete(TestData.Now.AddHours(3).AddMinutes(5)).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(SurgeryStatus.Completed);
        booking.MinutesInTheatre.ShouldBe(185 - 60);
    }

    [Fact]
    public void Time_out_requires_the_sign_in_first() =>
        TestData.ReadyForSignIn().ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff()).Error!.Code.ShouldBe("surgery.phase_order");

    [Fact]
    public void Sign_out_is_only_possible_during_surgery()
    {
        var booking = TestData.ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff()).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff()).IsSuccess.ShouldBeTrue();

        booking.ConfirmSafety(SafetyPhase.SignOut, Phase(SafetyPhase.SignOut), TestData.Staff()).Error!.Code.ShouldBe("surgery.not_in_progress");
    }

    [Fact]
    public void Sign_in_requires_a_complete_pre_operative_checklist()
    {
        var booking = TestData.Scheduled();
        booking.RecordPreOp([new ChecklistInput("consent", ChecklistItemState.Done, null)], TestData.Staff()).IsSuccess.ShouldBeTrue();

        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff()).Error!.Code.ShouldBe("surgery.preop_incomplete");
    }

    [Fact]
    public void Phase_completes_only_when_every_item_is_confirmed()
    {
        var booking = TestData.ReadyForSignIn();
        var allButOne = Phase(SafetyPhase.SignIn).Skip(1).ToList();

        booking.ConfirmSafety(SafetyPhase.SignIn, allButOne, TestData.Staff()).IsSuccess.ShouldBeTrue();
        booking.SignInCompletedAt.ShouldBeNull();

        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn).Take(1).ToList(), TestData.Staff(TestData.Now.AddMinutes(5))).IsSuccess.ShouldBeTrue();
        booking.SignInCompletedAt.ShouldBe(TestData.Now.AddMinutes(5));
    }

    [Fact]
    public void Each_item_records_who_confirmed_it_and_when()
    {
        var booking = TestData.ReadyForSignIn();
        var nurse = TestData.Staff(TestData.Now.AddMinutes(2), "Sr. Mary");

        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), nurse).IsSuccess.ShouldBeTrue();

        booking.SafetyChecks.Where(e => e.Phase == SafetyPhase.SignIn).ShouldAllBe(e =>
            e.ConfirmedByName == "Sr. Mary" && e.ConfirmedByUserId == nurse.UserId && e.ConfirmedAt == nurse.At);
    }

    [Fact]
    public void Resubmitting_a_confirmed_item_keeps_the_original_attestation()
    {
        var booking = TestData.ReadyForSignIn();
        var first = TestData.Staff(TestData.Now, "Dr. First");
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn).Take(2).ToList(), first).IsSuccess.ShouldBeTrue();

        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(TestData.Now.AddMinutes(9), "Dr. Second")).IsSuccess.ShouldBeTrue();

        var key = SurgicalSafetyChecklist.Phases[SafetyPhase.SignIn][0].Key;
        booking.SafetyChecks.Single(e => e.Phase == SafetyPhase.SignIn && e.Key == key).ConfirmedByName.ShouldBe("Dr. First");
    }

    [Fact]
    public void Completed_phase_is_locked()
    {
        var booking = TestData.ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff()).IsSuccess.ShouldBeTrue();

        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff()).Error!.Code.ShouldBe("surgery.phase_locked");
    }

    [Fact]
    public void Confirmed_items_cannot_be_withdrawn()
    {
        var booking = TestData.ReadyForSignIn();
        var pending = new ChecklistInput(SurgicalSafetyChecklist.Phases[SafetyPhase.SignIn][0].Key, ChecklistItemState.Pending, null);

        booking.ConfirmSafety(SafetyPhase.SignIn, [pending], TestData.Staff()).Error!.Code.ShouldBe("checklist.cannot_reset");
    }

    [Fact]
    public void Not_applicable_is_refused_for_items_that_always_apply()
    {
        var booking = TestData.ReadyForSignIn();

        var result = booking.ConfirmSafety(SafetyPhase.SignIn, [new ChecklistInput("pulse_oximeter", ChecklistItemState.NotApplicable, "Not needed")], TestData.Staff());

        result.Error!.Code.ShouldBe("checklist.not_applicable_refused");
    }

    [Fact]
    public void Not_applicable_needs_a_reason()
    {
        var booking = TestData.ReadyForSignIn();

        var withoutReason = booking.ConfirmSafety(SafetyPhase.SignIn, [new ChecklistInput("site_marked", ChecklistItemState.NotApplicable, " ")], TestData.Staff());
        var withReason = booking.ConfirmSafety(SafetyPhase.SignIn, [new ChecklistInput("site_marked", ChecklistItemState.NotApplicable, "Midline incision")], TestData.Staff());

        withoutReason.IsFailure.ShouldBeTrue();
        withReason.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Unknown_checklist_items_are_rejected() =>
        TestData.ReadyForSignIn().ConfirmSafety(SafetyPhase.SignIn, [new ChecklistInput("made_up", ChecklistItemState.Done, null)], TestData.Staff())
            .Error!.Type.ShouldBe(ErrorType.Validation);

    [Fact]
    public void Pre_operative_checklist_is_locked_after_sign_in()
    {
        var booking = TestData.ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff()).IsSuccess.ShouldBeTrue();

        booking.RecordPreOp([new ChecklistInput("npo", ChecklistItemState.Pending, null)], TestData.Staff()).Error!.Code.ShouldBe("surgery.preop_locked");
    }

    [Fact]
    public void Postponing_resets_the_day_of_surgery_checks()
    {
        var booking = TestData.ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff()).IsSuccess.ShouldBeTrue();

        booking.Postpone("Anaesthesia machine fault").IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(SurgeryStatus.Postponed);
        booking.SignInCompletedAt.ShouldBeNull();
        booking.InRoomAt.ShouldBeNull();
        booking.SafetyChecks.ShouldAllBe(e => e.State == ChecklistItemState.Pending);
        PreOpChecklist.IsComplete(booking.PreOpChecks).ShouldBeTrue("pre-op answers are kept with their timestamps");
    }

    [Fact]
    public void Checklist_has_the_three_who_phases_with_their_items()
    {
        SurgicalSafetyChecklist.Phases.Keys.ShouldBe([SafetyPhase.SignIn, SafetyPhase.TimeOut, SafetyPhase.SignOut], ignoreOrder: true);
        SurgicalSafetyChecklist.Phases.Values.ShouldAllBe(items => items.Count >= 5);
        SurgicalSafetyChecklist.Phases.Values.SelectMany(i => i).Select(i => i.Key).ShouldContain("antibiotic_prophylaxis");
    }
}
