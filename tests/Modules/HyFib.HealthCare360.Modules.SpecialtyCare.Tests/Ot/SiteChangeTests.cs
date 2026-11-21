using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>
/// A change of side or procedure after the WHO Sign-in (maker-checker slice 10): only the operating surgeon or the anaesthetist,
/// with a reason, and the checklist is redone; never after the incision.
/// </summary>
public sealed class SiteChangeTests
{
    private static readonly Guid Surgeon = Guid.CreateVersion7();
    private static readonly Guid Anaesthetist = Guid.CreateVersion7();
    private static readonly SurgicalSite Left = new(null, "Inguinal hernia repair", Laterality.Left, null);

    private static Result Check(SurgeryStatus status, bool signedIn, SurgicalSite asked, Guid? by, string? reason = "Consent and imaging say right") =>
        SiteChangeRule.Check(status, signedIn, Left, asked, Surgeon, Anaesthetist, by, reason);

    [Fact]
    public void Before_the_sign_in_the_plan_is_edited_as_usual() =>
        Check(SurgeryStatus.InPreOp, false, Left with { Laterality = Laterality.Right }, by: null, reason: null).IsSuccess.ShouldBeTrue();

    [Fact]
    public void After_the_sign_in_only_the_surgeon_or_the_anaesthetist_changes_the_side()
    {
        var right = Left with { Laterality = Laterality.Right };

        Check(SurgeryStatus.InPreOp, true, right, Guid.CreateVersion7()).Error!.Code.ShouldBe("surgery.site_change_team");
        Check(SurgeryStatus.InPreOp, true, right, null).Error!.Type.ShouldBe(ErrorType.Forbidden);
        Check(SurgeryStatus.InPreOp, true, right, Surgeon).IsSuccess.ShouldBeTrue();
        Check(SurgeryStatus.Scheduled, true, right, Anaesthetist).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void After_the_sign_in_a_change_needs_a_reason() =>
        Check(SurgeryStatus.InPreOp, true, Left with { ProcedureName = "Femoral hernia repair" }, Surgeon, reason: " ").Error!.Fields!.ShouldContainKey("changeReason");

    [Fact]
    public void A_change_of_level_or_procedure_counts_as_a_change_of_site()
    {
        Check(SurgeryStatus.InPreOp, true, Left with { Level = "L4–L5" }, Guid.CreateVersion7()).IsFailure.ShouldBeTrue();
        Check(SurgeryStatus.InPreOp, true, Left with { ProcedureServiceItemId = Guid.CreateVersion7() }, Guid.CreateVersion7()).IsFailure.ShouldBeTrue();
        Check(SurgeryStatus.InPreOp, true, Left with { ProcedureName = " inguinal HERNIA repair " }, Guid.CreateVersion7()).IsSuccess
            .ShouldBeTrue("the same procedure written differently is no change");
    }

    [Fact]
    public void After_the_incision_the_side_is_not_changed() =>
        Check(SurgeryStatus.InProgress, true, Left with { Laterality = Laterality.Right }, Surgeon).Error!.Code.ShouldBe("surgery.site_after_incision");

    [Fact]
    public void Voiding_the_checklist_keeps_what_was_confirmed_and_asks_for_it_again()
    {
        var booking = TestData.ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, TestData.AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignIn]), TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, TestData.AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.TimeOut]), TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();
        var before = booking.Site;
        booking.Laterality = Laterality.Right;

        var voided = booking.VoidChecklistForSiteChange(before, "Consent says right", TestData.Staff(name: "Dr. Rao"));

        (booking.SignInCompletedAt, booking.TimeOutCompletedAt).ShouldBe((null, null));
        booking.SafetyChecks.ShouldAllBe(e => e.State == ChecklistItemState.Pending);
        booking.Start(TestData.Now, TestData.Consents).Error!.Code.ShouldBe("surgery.checklist_incomplete");
        voided.Phases.ShouldBe([SafetyPhase.SignIn, SafetyPhase.TimeOut]);
        voided.Voided.Count.ShouldBe(SurgicalSafetyChecklist.Phases[SafetyPhase.SignIn].Count + SurgicalSafetyChecklist.Phases[SafetyPhase.TimeOut].Count);
        (voided.Before, voided.After, voided.ByName).ShouldBe(("Laparoscopic cholecystectomy", "Laparoscopic cholecystectomy, right", "Dr. Rao"));
        booking.ChecklistVoids.ShouldHaveSingleItem().ShouldBe(voided);
        booking.ConfirmSafety(SafetyPhase.SignIn, TestData.AllDone(SurgicalSafetyChecklist.Phases[SafetyPhase.SignIn]), TestData.Staff(), TestData.Consents).IsSuccess
            .ShouldBeTrue("the Sign-in is done again");
    }

    // ---------------------------------------------------------------- the anaesthesia technique and the priority after the Sign-in

    private static Result CheckCare(bool signedIn, AnaesthesiaType anaesthesia, SurgeryPriority priority, Guid? by, string? reason = "Spinal failed twice; converting") =>
        TechniqueOrPriorityChangeRule.Check(signedIn, AnaesthesiaType.Spinal, anaesthesia, SurgeryPriority.Elective, priority, Surgeon, Anaesthetist, by, reason);

    [Fact]
    public void Before_the_sign_in_the_anaesthesia_and_the_priority_are_edited_as_usual() =>
        CheckCare(false, AnaesthesiaType.Local, SurgeryPriority.Emergency, by: null, reason: null).IsSuccess.ShouldBeTrue();

    [Fact]
    public void After_the_sign_in_only_the_surgeon_or_the_anaesthetist_changes_the_anaesthesia_or_the_priority_with_a_reason()
    {
        // Local anaesthesia would drop the consent to anaesthesia; an emergency opens the waiver of consent.
        var toLocal = CheckCare(true, AnaesthesiaType.Local, SurgeryPriority.Elective, Guid.CreateVersion7());
        (toLocal.Error!.Code, toLocal.Error.Type).ShouldBe(("surgery.plan_change_team", ErrorType.Forbidden));
        CheckCare(true, AnaesthesiaType.Spinal, SurgeryPriority.Emergency, null).Error!.Code.ShouldBe("surgery.plan_change_team");
        CheckCare(true, AnaesthesiaType.Local, SurgeryPriority.Elective, Anaesthetist, reason: " ").Error!.Fields!.ShouldContainKey("changeReason");

        CheckCare(true, AnaesthesiaType.GA, SurgeryPriority.Elective, Anaesthetist).IsSuccess.ShouldBeTrue();
        CheckCare(true, AnaesthesiaType.Spinal, SurgeryPriority.Emergency, Surgeon).IsSuccess.ShouldBeTrue();
        CheckCare(true, AnaesthesiaType.Spinal, SurgeryPriority.Elective, by: null, reason: null).IsSuccess.ShouldBeTrue("nothing changes");
    }

    [Fact]
    public void A_change_of_anaesthesia_or_priority_is_described_for_the_audit_trail()
    {
        TechniqueOrPriorityChangeRule.Describe(AnaesthesiaType.Spinal, AnaesthesiaType.Local, SurgeryPriority.Elective, SurgeryPriority.Emergency)
            .ShouldBe("anaesthesia Spinal → Local; priority Elective → Emergency");
        TechniqueOrPriorityChangeRule.Describe(AnaesthesiaType.GA, AnaesthesiaType.GA, SurgeryPriority.Urgent, SurgeryPriority.Emergency)
            .ShouldBe("priority Urgent → Emergency");
    }
}
