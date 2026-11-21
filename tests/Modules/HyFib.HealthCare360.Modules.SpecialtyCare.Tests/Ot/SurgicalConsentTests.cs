using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>
/// Informed consent before surgery and anaesthesia (NABH COP), checked by the theatre's gates rather than ticked: the pre-op
/// "consent" item, the WHO Sign-in and Time-out and the incision need the patient's consent to the procedure, and to the
/// anaesthesia unless the surgeon gives it locally, recorded since the operation was booked, not withdrawn and not refused
/// since. In an emergency the operating surgeon may go ahead without it, with the clinical reason and a second doctor who agrees.
/// </summary>
public sealed class SurgicalConsentTests
{
    private const string Required = "surgery.consent_required";
    private const string Reason = "Ruptured ectopic, in shock; no relative reachable";

    private static IReadOnlyList<ChecklistInput> Phase(SafetyPhase phase) => TestData.AllDone(SurgicalSafetyChecklist.Phases[phase]);

    private static readonly ChecklistInput ConsentTicked = new(PreOpChecklist.ConsentItem, ChecklistItemState.Done, null);

    /// <summary>Signed in and timed out, the patient's consents on record: the incision is next.</summary>
    private static SurgeryBooking ReadyToStart()
    {
        var booking = TestData.ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(TestData.Now.AddMinutes(60)), TestData.Consents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(TestData.Now.AddMinutes(75)), TestData.Consents).IsSuccess.ShouldBeTrue();
        return booking;
    }

    [Fact]
    public void A_refusal_recorded_before_the_booking_is_carried_on_the_check_but_changes_nothing()
    {
        var booking = TestData.Scheduled();
        var earlier = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(-3), granted: false) with { CapturedByName = "Dr Casualty" };
        var older = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(-5), granted: false);

        var status = booking.Consent([older, earlier]);

        var procedure = status.Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure);
        procedure.State.ShouldBe(SurgicalConsentState.Missing);
        procedure.EarlierRefusal.ShouldBe(new EarlierRefusal(earlier.Id, earlier.CapturedAt, "Dr Casualty"), "the patient's latest refusal before the booking");
        status.Checks.Single(c => c.Kind == SurgicalConsentKind.Anaesthesia).EarlierRefusal.ShouldBeNull();
        (status.Satisfied, status.SaidNo, status.Refused).ShouldBe((false, false, false));
    }

    [Fact]
    public void An_earlier_refusal_is_not_carried_once_a_consent_or_refusal_is_recorded_since_the_booking()
    {
        var booking = TestData.Scheduled();
        var earlier = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(-3), granted: false);

        var consented = booking.Consent([earlier, TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(1))]);
        consented.Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure).EarlierRefusal.ShouldBeNull();

        var refused = booking.Consent([earlier, TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(1), granted: false)]);
        var check = refused.Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure);
        (check.State, check.EarlierRefusal).ShouldBe((SurgicalConsentState.Refused, (EarlierRefusal?)null));
    }

    [Fact]
    public void A_nurse_cannot_tick_consent_signed_when_no_consent_is_recorded()
    {
        var booking = TestData.Scheduled();

        var ticked = booking.RecordPreOp([ConsentTicked], TestData.Staff(), TestData.NoConsents);

        ticked.IsSuccess.ShouldBeFalse("\"consent signed\" is confirmed only over a consent the front office recorded, never on a tick alone");
        ticked.Error!.Code.ShouldBe(Required);
        ticked.Error.Message.ShouldContain("consent to the procedure (high-risk procedure consent) is not recorded");
        ticked.Error.Message.ShouldContain("consent to anaesthesia is not recorded");
        booking.PreOpChecks.Single(e => e.Key == PreOpChecklist.ConsentItem).State.ShouldBe(ChecklistItemState.Pending);
    }

    [Fact]
    public void The_incision_does_not_go_ahead_without_a_recorded_consent()
    {
        var booking = ReadyToStart();

        var started = booking.Start(TestData.Now.AddMinutes(80), TestData.NoConsents);

        started.IsSuccess.ShouldBeFalse("the incision waits for the patient's recorded consent (NABH: informed consent before surgery and anaesthesia)");
        started.Error!.Code.ShouldBe(Required);
        booking.Status.ShouldBe(SurgeryStatus.InPreOp);
        booking.StartedAt.ShouldBeNull();
    }

    [Fact]
    public void The_sign_in_and_the_time_out_wait_for_the_consent()
    {
        var booking = TestData.ReadyForSignIn();

        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), TestData.NoConsents).Error!.Code.ShouldBe(Required);
        booking.SafetyChecks.ShouldAllBe(e => e.State == ChecklistItemState.Pending);
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();

        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), TestData.NoConsents).Error!.Code.ShouldBe(Required);
        booking.TimeOutCompletedAt.ShouldBeNull();
    }

    [Fact]
    public void A_consent_given_since_the_booking_opens_every_gate_and_the_incision_names_it()
    {
        var booking = TestData.Scheduled();
        var procedure = TestData.Consent(SurgicalConsentKind.Procedure);
        var anaesthesia = TestData.Consent(SurgicalConsentKind.Anaesthesia);
        IReadOnlyCollection<ConsentOnRecord> consents = [procedure, anaesthesia];

        booking.RecordPreOp([ConsentTicked], TestData.Staff(), consents).IsSuccess.ShouldBeTrue();
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(), consents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), consents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), consents).IsSuccess.ShouldBeTrue();
        booking.Start(TestData.Now.AddMinutes(80), consents).IsSuccess.ShouldBeTrue();

        booking.Status.ShouldBe(SurgeryStatus.InProgress);
        (booking.ProcedureConsentId, booking.AnaesthesiaConsentId).ShouldBe((procedure.Id, anaesthesia.Id));
    }

    [Fact]
    public void A_withdrawn_consent_does_not_count()
    {
        var booking = ReadyToStart();
        IReadOnlyCollection<ConsentOnRecord> withdrawn =
        [
            TestData.Consent(SurgicalConsentKind.Procedure, revokedAt: TestData.Now.AddMinutes(70)),
            TestData.Consent(SurgicalConsentKind.Anaesthesia),
        ];

        var started = booking.Start(TestData.Now.AddMinutes(80), withdrawn);

        started.Error!.Code.ShouldBe(Required);
        started.Error.Message.ShouldContain("consent to the procedure (high-risk procedure consent) was withdrawn");
        booking.Consent(withdrawn).Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure).State.ShouldBe(SurgicalConsentState.Withdrawn);
    }

    [Fact]
    public void A_consent_given_before_the_operation_was_booked_does_not_count()
    {
        var booking = ReadyToStart();
        IReadOnlyCollection<ConsentOnRecord> older =
        [
            TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(-1)),
            TestData.Consent(SurgicalConsentKind.Anaesthesia),
        ];

        var started = booking.Start(TestData.Now.AddMinutes(80), older);

        started.Error!.Code.ShouldBe(Required);
        started.Error.Message.ShouldContain("was recorded before this operation was booked");
        booking.Consent(older).Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure).State.ShouldBe(SurgicalConsentState.BeforeBooking);
    }

    [Fact]
    public void A_refusal_recorded_after_the_consent_takes_it_back_until_the_patient_consents_again()
    {
        var booking = ReadyToStart();
        var anaesthesia = TestData.Consent(SurgicalConsentKind.Anaesthesia);
        List<ConsentOnRecord> consents =
        [
            TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddHours(-3)),
            TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddHours(-1), granted: false),
            anaesthesia,
        ];

        booking.Start(TestData.Now.AddMinutes(80), consents).Error!.Message.ShouldContain("the patient refused consent to the procedure");
        booking.Consent(consents).Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure).State.ShouldBe(SurgicalConsentState.Refused);

        var again = TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddMinutes(-30));
        consents.Add(again);
        booking.Start(TestData.Now.AddMinutes(80), consents).IsSuccess.ShouldBeTrue();
        booking.ProcedureConsentId.ShouldBe(again.Id);
    }

    // ---------------------------------------------------------------- the patient's latest word, for this operation

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_withdrawal_is_the_patients_latest_word_though_another_consent_is_still_on_record(bool laterWithdrawn)
    {
        // The procedure consent was recorded twice (by a duplicate entry, or once per planned operation); the patient
        // withdraws, and the front office withdraws one of the two.
        var booking = ReadyToStart();
        var withdrawnAt = TestData.BookedAt.AddHours(5);
        var first = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(1), revokedAt: laterWithdrawn ? null : withdrawnAt);
        var second = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(2), revokedAt: laterWithdrawn ? withdrawnAt : null);
        List<ConsentOnRecord> consents = [first, second, TestData.Consent(SurgicalConsentKind.Anaesthesia)];

        var started = booking.Start(TestData.Now.AddMinutes(80), consents);

        started.Error!.Code.ShouldBe(Required, "the other consent on record was given before the withdrawal: it does not undo it");
        started.Error.Message.ShouldContain("consent to the procedure (high-risk procedure consent) was withdrawn");
        var procedure = booking.Consent(consents).Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure);
        (procedure.State, procedure.Consent).ShouldBe((SurgicalConsentState.Withdrawn, laterWithdrawn ? second : first));
        (booking.Status, booking.ProcedureConsentId).ShouldBe((SurgeryStatus.InPreOp, null));

        // Consented again after the withdrawal, the operation goes ahead on that consent.
        var again = TestData.Consent(SurgicalConsentKind.Procedure, withdrawnAt.AddHours(1));
        consents.Add(again);
        booking.Start(TestData.Now.AddMinutes(80), consents).IsSuccess.ShouldBeTrue();
        booking.ProcedureConsentId.ShouldBe(again.Id);
    }

    [Fact]
    public void The_later_of_a_refusal_and_a_withdrawal_is_the_patients_latest_word()
    {
        var booking = TestData.Scheduled();
        var given = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(1), revokedAt: TestData.BookedAt.AddHours(3));
        var refused = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(2), granted: false);
        SurgicalConsentState Procedure(params ConsentOnRecord[] consents) =>
            booking.Consent(consents).Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure).State;

        Procedure(given, refused).ShouldBe(SurgicalConsentState.Withdrawn);
        Procedure(given, refused with { CapturedAt = TestData.BookedAt.AddHours(4) }).ShouldBe(SurgicalConsentState.Refused);
        Procedure(given, refused, TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(3)))
            .ShouldBe(SurgicalConsentState.Withdrawn, "a consent given at the moment of the withdrawal is not after it");
        Procedure(given, refused, TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(6))).ShouldBe(SurgicalConsentState.Recorded);
    }

    [Fact]
    public void A_consent_another_operations_incision_relied_on_does_not_open_this_ones_gates()
    {
        // Two operations booked before their consents: the other one's incision relied on the consents recorded since.
        var booking = TestData.Scheduled();
        List<ConsentOnRecord> consents =
        [
            TestData.Consent(SurgicalConsentKind.Procedure) with { ReliedOnBy = "NHOT2600012" },
            TestData.Consent(SurgicalConsentKind.Anaesthesia) with { ReliedOnBy = "NHOT2600012" },
        ];

        var ticked = booking.RecordPreOp([ConsentTicked], TestData.Staff(), consents);

        ticked.Error!.Code.ShouldBe(Required);
        ticked.Error.Message.ShouldContain(
            "consent to the procedure (high-risk procedure consent) was relied on by another operation (NHOT2600012); take it again for this operation");
        booking.Consent(consents).Checks.ShouldAllBe(c => c.State == SurgicalConsentState.OtherOperation);
        booking.PreOpChecks.Single(e => e.Key == PreOpChecklist.ConsentItem).State.ShouldBe(ChecklistItemState.Pending);

        // Taken again for this operation, it goes ahead on its own consents.
        var procedure = TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddHours(-1));
        var anaesthesia = TestData.Consent(SurgicalConsentKind.Anaesthesia, TestData.Now.AddHours(-1));
        consents.AddRange([procedure, anaesthesia]);
        booking.RecordPreOp([ConsentTicked], TestData.Staff(), consents).IsSuccess.ShouldBeTrue();
        var status = booking.Consent(consents);
        (status.ConsentIdOf(SurgicalConsentKind.Procedure), status.ConsentIdOf(SurgicalConsentKind.Anaesthesia)).ShouldBe(((Guid?)procedure.Id, (Guid?)anaesthesia.Id));
    }

    [Fact]
    public void Consent_signed_cannot_be_answered_with_a_value_that_is_not_an_answer()
    {
        // A number the JSON enum converter lets through, neither done, not applicable nor pending.
        var booking = TestData.Scheduled();
        var crafted = new ChecklistInput(PreOpChecklist.ConsentItem, (ChecklistItemState)7, null);

        booking.RecordPreOp([crafted], TestData.Staff(), TestData.NoConsents).Error!.Code.ShouldBe(Required,
            "the consent gate runs for any answer but pending, not only for done");
        booking.RecordPreOp([crafted], TestData.Staff(), TestData.Consents).Error!.Fields!.ShouldContainKey("items");
        booking.PreOpChecks.Single(e => e.Key == PreOpChecklist.ConsentItem).State.ShouldBe(ChecklistItemState.Pending);
    }

    [Theory]
    [InlineData(AnaesthesiaType.GA)]
    [InlineData(AnaesthesiaType.Spinal)]
    [InlineData(AnaesthesiaType.Epidural)]
    [InlineData(AnaesthesiaType.Regional)]
    [InlineData(AnaesthesiaType.Sedation)]
    public void Consent_to_anaesthesia_is_needed_for_every_technique_the_anaesthetist_gives(AnaesthesiaType anaesthesia)
    {
        var booking = TestData.NewBooking(anaesthesia);
        IReadOnlyCollection<ConsentOnRecord> procedureOnly = [TestData.Consent(SurgicalConsentKind.Procedure)];

        var ticked = booking.RecordPreOp([ConsentTicked], TestData.Staff(), procedureOnly);

        ticked.Error!.Code.ShouldBe(Required);
        ticked.Error.Message.ShouldContain("consent to anaesthesia is not recorded");
        booking.Consent(procedureOnly).Missing.ShouldHaveSingleItem().Kind.ShouldBe(SurgicalConsentKind.Anaesthesia);
    }

    [Theory]
    [InlineData(AnaesthesiaType.Local)]
    [InlineData(AnaesthesiaType.Topical)]
    public void Local_or_topical_anaesthesia_given_by_the_surgeon_needs_the_procedure_consent_only(AnaesthesiaType anaesthesia)
    {
        var booking = TestData.NewBooking(anaesthesia);
        booking.Schedule(Guid.CreateVersion7(), TestData.Now.AddHours(1), TestData.Now.AddHours(2), TestData.Now).IsSuccess.ShouldBeTrue();
        IReadOnlyCollection<ConsentOnRecord> procedureOnly = [TestData.Consent(SurgicalConsentKind.Procedure)];

        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(), procedureOnly).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), procedureOnly).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), procedureOnly).IsSuccess.ShouldBeTrue();
        booking.Start(TestData.Now.AddMinutes(80), procedureOnly).IsSuccess.ShouldBeTrue();

        booking.Consent(procedureOnly).Checks.Single(c => c.Kind == SurgicalConsentKind.Anaesthesia).State.ShouldBe(SurgicalConsentState.NotNeeded);
        booking.AnaesthesiaConsentId.ShouldBeNull();
        TestData.NewBooking(anaesthesia).RecordPreOp([ConsentTicked], TestData.Staff(), TestData.NoConsents).Error!.Code.ShouldBe(Required);
    }

    // ---------------------------------------------------------------- the emergency doctrine

    private static SurgeryBooking Emergency()
    {
        var booking = TestData.Scheduled();
        booking.Priority = SurgeryPriority.Emergency;
        return booking;
    }

    private static StaffStamp Surgeon(DateTimeOffset? at = null) => TestData.Staff(at, "Dr. Arjun Rao");

    private static ConcurringDoctor SecondDoctor(SurgeryBooking booking) => new(Guid.CreateVersion7(), "Dr. Kavita Shah", booking.AnaesthetistId, IsDoctor: true);

    [Fact]
    public void An_emergency_operation_goes_ahead_without_consent_when_the_surgeon_waives_it_with_a_second_doctor()
    {
        var booking = Emergency();
        var surgeon = Surgeon();
        var second = SecondDoctor(booking);

        booking.WaiveConsent($"  {Reason} ", surgeon, booking.SurgeonId, second, TestData.NoConsents).IsSuccess.ShouldBeTrue();

        booking.ConsentWaiver.ShouldBe(new SurgicalConsentWaiver(surgeon.At, surgeon.UserId, "Dr. Arjun Rao", second.UserId, "Dr. Kavita Shah", Reason));
        booking.Consent(TestData.NoConsents).Satisfied.ShouldBeTrue();
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.Start(TestData.Now.AddMinutes(30), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        (booking.ProcedureConsentId, booking.AnaesthesiaConsentId).ShouldBe((null, null));
    }

    [Fact]
    public void A_waiver_stands_only_while_the_operation_is_an_emergency()
    {
        var booking = Emergency();
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();

        booking.Priority = SurgeryPriority.Urgent;

        booking.Consent(TestData.NoConsents).Satisfied.ShouldBeFalse("no longer an emergency, the operation waits for the patient's consent");
        booking.RecordPreOp([ConsentTicked], TestData.Staff(), TestData.NoConsents).Error!.Code.ShouldBe(Required);
        booking.ConsentWaiver.ShouldNotBeNull("the waiver stays on the record");
        booking.RecordPreOp([ConsentTicked], TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_postponement_ends_the_waiver_because_the_operation_waited()
    {
        var booking = Emergency();
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();

        booking.Postpone("Theatre taken by a more urgent case").IsSuccess.ShouldBeTrue();
        booking.Schedule(Guid.CreateVersion7(), TestData.Now.AddHours(4), TestData.Now.AddHours(6), TestData.Now).IsSuccess.ShouldBeTrue();

        booking.ConsentWaiver.ShouldBeNull("the operation waited: the consent is taken, or waived again, for the new slot");
        booking.Consent(TestData.NoConsents).Satisfied.ShouldBeFalse();
        booking.RecordPreOp([ConsentTicked], TestData.Staff(), TestData.NoConsents).Error!.Code.ShouldBe(Required);
        booking.WaiveConsent(Reason, Surgeon(TestData.Now.AddHours(3)), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents)
            .IsSuccess.ShouldBeTrue("if it still cannot wait, the surgeon and a second doctor waive it again");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Emergency")]
    [InlineData(" 123456789 ")]
    public void The_waiver_needs_the_clinical_reason_of_at_least_ten_characters(string? reason)
    {
        var booking = Emergency();

        var waived = booking.WaiveConsent(reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents);

        waived.Error!.Fields!.ShouldContainKey("reason");
        booking.ConsentWaiver.ShouldBeNull();
    }

    [Fact]
    public void The_doctor_who_agrees_is_a_second_doctor_never_the_surgeon()
    {
        var booking = Emergency();
        var surgeon = Surgeon();

        booking.WaiveConsent(Reason, surgeon, booking.SurgeonId, new ConcurringDoctor(surgeon.UserId!.Value, "Dr. Arjun Rao", booking.SurgeonId, true), TestData.NoConsents)
            .Error!.Code.ShouldBe("surgery.consent_waiver_same_doctor");
        booking.WaiveConsent(Reason, surgeon, booking.SurgeonId, new ConcurringDoctor(Guid.CreateVersion7(), "Dr. A. Rao (second account)", booking.SurgeonId, true),
            TestData.NoConsents).Error!.Code.ShouldBe("surgery.consent_waiver_same_doctor");
        booking.WaiveConsent(Reason, surgeon, booking.SurgeonId, new ConcurringDoctor(Guid.CreateVersion7(), "Sr. Leela Thomas", Guid.CreateVersion7(), false),
            TestData.NoConsents).Error!.Code.ShouldBe("surgery.consent_waiver_not_doctor");
        booking.ConsentWaiver.ShouldBeNull();
    }

    [Fact]
    public void Only_the_operating_surgeon_waives_consent_from_their_own_sign_in()
    {
        var booking = Emergency();

        var byAnother = booking.WaiveConsent(Reason, TestData.Staff(name: "Dr. Other"), Guid.CreateVersion7(), SecondDoctor(booking), TestData.NoConsents);
        var byNoDoctor = booking.WaiveConsent(Reason, TestData.Staff(name: "Sr. Anita Joseph"), null, SecondDoctor(booking), TestData.NoConsents);

        (byAnother.Error!.Code, byAnother.Error.Type).ShouldBe(("surgery.consent_waiver_surgeon", ErrorType.Forbidden));
        byNoDoctor.Error!.Code.ShouldBe("surgery.consent_waiver_surgeon");
        booking.ConsentWaiver.ShouldBeNull();
    }

    [Theory]
    [InlineData(SurgeryPriority.Elective)]
    [InlineData(SurgeryPriority.Urgent)]
    public void Only_an_emergency_operation_goes_ahead_without_consent(SurgeryPriority priority)
    {
        var booking = TestData.Scheduled();
        booking.Priority = priority;

        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).Error!.Code.ShouldBe("surgery.consent_waiver_not_emergency");
        booking.ConsentWaiver.ShouldBeNull();
    }

    [Fact]
    public void Nothing_is_waived_when_the_consent_is_recorded()
    {
        var booking = Emergency();

        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.Consents).Error!.Code.ShouldBe("surgery.consent_waiver_not_needed");
        booking.ConsentWaiver.ShouldBeNull();
    }

    [Fact]
    public void Consent_is_waived_once_and_only_before_the_incision()
    {
        var booking = Emergency();
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();

        booking.WaiveConsent(Reason, Surgeon(TestData.Now.AddMinutes(5)), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents)
            .Error!.Code.ShouldBe("surgery.consent_waived");

        var started = ReadyToStart();
        started.Priority = SurgeryPriority.Emergency;
        started.Start(TestData.Now.AddMinutes(80), TestData.Consents).IsSuccess.ShouldBeTrue();
        started.WaiveConsent(Reason, Surgeon(), started.SurgeonId, SecondDoctor(started), TestData.NoConsents).Error!.Code.ShouldBe("surgery.consent_waiver_closed");
    }

    [Fact]
    public void The_emergency_waiver_does_not_override_a_refusal_the_patient_recorded()
    {
        var booking = Emergency();
        List<ConsentOnRecord> consents = [TestData.Consent(SurgicalConsentKind.Anaesthesia, granted: false)];

        var waived = booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), consents);

        (waived.Error!.Code, waived.Error.Type).ShouldBe(("surgery.consent_refused", ErrorType.BusinessRule));
        waived.Error.Message.ShouldContain("The patient refused consent to anaesthesia");
        waived.Error.Message.ShouldContain("record the new consent");
        booking.ConsentWaiver.ShouldBeNull();

        // The patient changes his mind: the front office records his consent, and what is still missing may be waived.
        consents.Add(TestData.Consent(SurgicalConsentKind.Anaesthesia, TestData.Now.AddMinutes(-30)));
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), consents).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_refusal_recorded_after_the_waiver_stops_the_operation()
    {
        var booking = Emergency();
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), TestData.NoConsents).IsSuccess.ShouldBeTrue();

        // The patient comes round and refuses the operation; the front office records it.
        IReadOnlyCollection<ConsentOnRecord> refused = [TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddMinutes(10), granted: false)];

        var timedOut = booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), refused);

        timedOut.Error!.Code.ShouldBe(Required);
        timedOut.Error.Message.ShouldContain("the patient refused consent to the procedure");
        timedOut.Error.Message.ShouldContain("An emergency does not override the patient's refusal");
        var status = booking.Consent(refused);
        (status.Satisfied, status.Refused).ShouldBe((false, true));
        booking.ConsentWaiver.ShouldNotBeNull("the waiver stays on the record");
    }

    // ---------------------------------------------------------------- a withdrawal is the patient's "no", like a refusal

    [Fact]
    public void The_emergency_waiver_does_not_override_a_consent_the_patient_withdrew()
    {
        // The patient consented, then withdrew ("wants to speak to his son"); the front office recorded it.
        var booking = Emergency();
        List<ConsentOnRecord> consents = [TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddHours(-3), revokedAt: TestData.Now.AddHours(-1))];

        var waived = booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), consents);

        (waived.Error!.Code, waived.Error.Type).ShouldBe(("surgery.consent_withdrawn", ErrorType.BusinessRule));
        waived.Error.Message.ShouldContain("The patient withdrew consent to the procedure");
        waived.Error.Message.ShouldContain("an emergency does not override a withdrawal");
        booking.ConsentWaiver.ShouldBeNull();

        // Refused, consented again, then withdrawn: his latest word is still "no".
        consents =
        [
            TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddHours(-5), granted: false),
            TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddHours(-4), revokedAt: TestData.Now.AddHours(-2)),
        ];
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), consents).Error!.Code.ShouldBe("surgery.consent_withdrawn");
        booking.Consent(consents).Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure).State.ShouldBe(SurgicalConsentState.Withdrawn);

        // He agrees again: the new consent is his latest word, and what is still missing may be waived.
        consents.Add(TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddMinutes(-30)));
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), consents).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_withdrawal_recorded_after_the_waiver_stops_the_operation()
    {
        // Waived while the patient could not consent. He comes round and consents to both; then he withdraws his consent to
        // the operation, and the front office records it.
        var booking = Emergency();
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        var procedure = TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddMinutes(5));
        var anaesthesia = TestData.Consent(SurgicalConsentKind.Anaesthesia, TestData.Now.AddMinutes(5));
        IReadOnlyCollection<ConsentOnRecord> consented = [procedure, anaesthesia];
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(), consented).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), consented).IsSuccess.ShouldBeTrue();
        IReadOnlyCollection<ConsentOnRecord> withdrawn = [procedure with { RevokedAt = TestData.Now.AddMinutes(20) }, anaesthesia];

        var timedOut = booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(TestData.Now.AddMinutes(25)), withdrawn);

        timedOut.Error!.Code.ShouldBe(Required, "a withdrawal is the patient's no, as a refusal is: the waiver no longer stands");
        timedOut.Error.Message.ShouldContain("consent to the procedure (high-risk procedure consent) was withdrawn");
        timedOut.Error.Message.ShouldContain("An emergency does not override a consent the patient withdrew");
        timedOut.Error.Message.ShouldNotContain("may go ahead without it");
        var status = booking.Consent(withdrawn);
        (status.Satisfied, status.SaidNo, status.Refused, status.WaiverStands).ShouldBe((false, true, false, false));
        status.No.ShouldNotBeNull().Kind.ShouldBe(SurgicalConsentKind.Procedure);
        booking.TimeOutCompletedAt.ShouldBeNull();
        booking.ConsentWaiver.ShouldNotBeNull("the waiver stays on the record");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_refusal_and_a_withdrawal_after_the_waiver_stop_the_incision_in_whichever_order_they_are_recorded(bool refusalFirst)
    {
        // Waived, the day goes on without consent; then a consent to the operation is recorded. The patient says no: the
        // front office records a refusal and withdraws the earlier consent, in one order or the other.
        var booking = Emergency();
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        var (refusedAt, withdrawnAt) = refusalFirst
            ? (TestData.Now.AddMinutes(20), TestData.Now.AddMinutes(30))
            : (TestData.Now.AddMinutes(30), TestData.Now.AddMinutes(20));
        IReadOnlyCollection<ConsentOnRecord> consents =
        [
            TestData.Consent(SurgicalConsentKind.Procedure, TestData.Now.AddMinutes(10), revokedAt: withdrawnAt),
            TestData.Consent(SurgicalConsentKind.Procedure, refusedAt, granted: false),
        ];

        var started = booking.Start(TestData.Now.AddMinutes(40), consents);

        started.Error!.Code.ShouldBe(Required, "the order of two entries at the front office does not decide whether the operation goes ahead");
        var status = booking.Consent(consents);
        (status.Satisfied, status.SaidNo, status.Refused).ShouldBe((false, true, !refusalFirst));
        status.Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure).State
            .ShouldBe(refusalFirst ? SurgicalConsentState.Withdrawn : SurgicalConsentState.Refused, "the later of the two is the patient's latest word");
        (booking.Status, booking.StartedAt).ShouldBe((SurgeryStatus.Scheduled, (DateTimeOffset?)null));
    }

    [Fact]
    public void A_withdrawal_since_the_booking_counts_though_the_consent_withdrawn_was_given_before_it()
    {
        // The front office cannot tell which operation an older consent was for: withdrawing it is the patient's no to this
        // one too, and a consent given between the booking and the withdrawal does not undo it.
        var booking = ReadyToStart();
        var older = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(-1), revokedAt: TestData.BookedAt.AddHours(5));
        var given = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(1));
        var anaesthesia = TestData.Consent(SurgicalConsentKind.Anaesthesia);
        List<ConsentOnRecord> consents = [older, given, anaesthesia];

        var started = booking.Start(TestData.Now.AddMinutes(80), consents);

        started.Error!.Code.ShouldBe(Required);
        started.Error.Message.ShouldContain("consent to the procedure (high-risk procedure consent) was withdrawn");
        var procedure = booking.Consent(consents).Checks.Single(c => c.Kind == SurgicalConsentKind.Procedure);
        (procedure.State, procedure.Consent).ShouldBe((SurgicalConsentState.Withdrawn, older), "the screen shows the withdrawal of the older consent");

        // Withdrawn before this operation was booked, it was another operation's matter: the consent given since stands.
        booking.Consent([older with { RevokedAt = TestData.BookedAt.AddMinutes(-30) }, given, anaesthesia]).Recorded.ShouldBeTrue();

        // Consented after the withdrawal, the operation goes ahead on that consent.
        var again = TestData.Consent(SurgicalConsentKind.Procedure, TestData.BookedAt.AddHours(6));
        consents.Add(again);
        booking.Start(TestData.Now.AddMinutes(80), consents).IsSuccess.ShouldBeTrue();
        booking.ProcedureConsentId.ShouldBe(again.Id);
    }

    // ---------------------------------------------------------------- what the operation goes ahead on (the patient journey)

    [Fact]
    public void An_operation_whose_consent_to_anaesthesia_alone_was_waived_went_ahead_on_the_waiver()
    {
        var booking = Emergency();
        var procedure = TestData.Consent(SurgicalConsentKind.Procedure);
        IReadOnlyCollection<ConsentOnRecord> procedureOnly = [procedure];
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), procedureOnly).IsSuccess.ShouldBeTrue();
        booking.ReliesOnWaiver(procedureOnly).ShouldBeTrue("the consent to anaesthesia is not recorded: the gates stand on the waiver");

        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(), procedureOnly).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), procedureOnly).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), procedureOnly).IsSuccess.ShouldBeTrue();
        booking.Start(TestData.Now.AddMinutes(30), procedureOnly).IsSuccess.ShouldBeTrue();

        (booking.ProcedureConsentId, booking.AnaesthesiaConsentId).ShouldBe((procedure.Id, null));
        booking.ReliesOnWaiver(TestData.NoConsents).ShouldBeTrue("the incision relied on no consent to anaesthesia, which it needed");
        booking.ReliesOnWaiver(TestData.Consents).ShouldBeTrue("a consent recorded after the incision does not change what it relied on");
    }

    [Fact]
    public void A_waiver_overtaken_by_the_patients_consent_or_no_is_not_what_the_operation_goes_ahead_on()
    {
        var booking = Emergency();
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.ReliesOnWaiver(TestData.NoConsents).ShouldBeTrue();

        booking.ReliesOnWaiver(TestData.Consents).ShouldBeFalse("the consents recorded since: it goes ahead on them");
        booking.ReliesOnWaiver([TestData.Consent(SurgicalConsentKind.Procedure, granted: false)]).ShouldBeFalse("the patient refused since");
        booking.ReliesOnWaiver([TestData.Consent(SurgicalConsentKind.Procedure, revokedAt: TestData.Now.AddHours(-1))])
            .ShouldBeFalse("the patient withdrew since");
        booking.Priority = SurgeryPriority.Urgent;
        booking.ReliesOnWaiver(TestData.NoConsents).ShouldBeFalse("no longer an emergency, the waiver does not stand");

        // An incision on the consents recorded after the waiver did not go ahead on it.
        booking.Priority = SurgeryPriority.Emergency;
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();
        booking.Start(TestData.Now.AddMinutes(30), TestData.Consents).IsSuccess.ShouldBeTrue();
        booking.ReliesOnWaiver(TestData.NoConsents).ShouldBeFalse();

        // An operation started before the theatre read consents relied on none, and on no waiver either.
        var before = TestData.InProgress();
        (before.ProcedureConsentId, before.AnaesthesiaConsentId) = (null, null);
        before.ReliesOnWaiver(TestData.NoConsents).ShouldBeFalse();
    }

    [Fact]
    public void Moving_an_emergency_operation_to_another_slot_ends_the_waiver()
    {
        var booking = Emergency();
        var theatre = booking.TheatreId!.Value;
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();

        booking.Schedule(theatre, booking.ScheduledStart!.Value, TestData.Now.AddHours(4), TestData.Now).IsSuccess.ShouldBeTrue();
        booking.ConsentWaiver.ShouldNotBeNull("a longer slot is the same slot: the operation does not wait");

        booking.Schedule(theatre, TestData.Now.AddHours(5), TestData.Now.AddHours(7), TestData.Now).IsSuccess.ShouldBeTrue();
        booking.ConsentWaiver.ShouldBeNull("the operation waits: the consent is taken for the new slot, or waived again");
        booking.Consent(TestData.NoConsents).Satisfied.ShouldBeFalse();

        booking.WaiveConsent(Reason, Surgeon(TestData.Now.AddMinutes(5)), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();
        booking.Schedule(Guid.CreateVersion7(), booking.ScheduledStart!.Value, booking.ScheduledEnd!.Value, TestData.Now).IsSuccess.ShouldBeTrue();
        booking.ConsentWaiver.ShouldBeNull("another theatre is another slot");
    }

    [Fact]
    public void A_waiver_given_before_the_slot_stands_when_the_slot_is_given()
    {
        var booking = TestData.NewBooking();
        booking.Priority = SurgeryPriority.Emergency;
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();

        booking.Schedule(Guid.CreateVersion7(), TestData.Now.AddHours(1), TestData.Now.AddHours(2), TestData.Now).IsSuccess.ShouldBeTrue();

        booking.ConsentWaiver.ShouldNotBeNull();
        booking.Consent(TestData.NoConsents).Satisfied.ShouldBeTrue();
    }

    [Fact]
    public void Another_operating_surgeon_ends_the_waiver()
    {
        var booking = Emergency();
        booking.WaiveConsent(Reason, Surgeon(), booking.SurgeonId, SecondDoctor(booking), TestData.NoConsents).IsSuccess.ShouldBeTrue();

        booking.AssignSurgeon(booking.SurgeonId);
        booking.ConsentWaiver.ShouldNotBeNull("the same surgeon keeps the case");

        var other = Guid.CreateVersion7();
        booking.AssignSurgeon(other);

        (booking.SurgeonId, booking.ConsentWaiver).ShouldBe((other, null));
        booking.Consent(TestData.NoConsents).Satisfied.ShouldBeFalse("the waiver was the first surgeon's judgement");
        booking.WaiveConsent(Reason, Surgeon(TestData.Now.AddMinutes(5)), other, SecondDoctor(booking), TestData.NoConsents)
            .IsSuccess.ShouldBeTrue("the new surgeon waives it again, with a second doctor");
    }

    // ---------------------------------------------------------------- the anaesthesia the Sign-in was done under

    [Fact]
    public void A_consent_to_anaesthesia_needed_at_the_sign_in_stays_needed_when_the_technique_becomes_local()
    {
        var booking = TestData.ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();
        booking.AnaesthesiaConsentRequiredAtSignIn.ShouldBeTrue("the Sign-in was done under general anaesthesia");

        booking.AnaesthesiaType = AnaesthesiaType.Local;
        IReadOnlyCollection<ConsentOnRecord> procedureOnly = [TestData.Consent(SurgicalConsentKind.Procedure)];

        booking.ConsentsNeeded.ShouldBe([SurgicalConsentKind.Procedure, SurgicalConsentKind.Anaesthesia]);
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), procedureOnly).Error!.Message
            .ShouldContain("consent to anaesthesia is not recorded");
        booking.ConfirmSafety(SafetyPhase.TimeOut, Phase(SafetyPhase.TimeOut), TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();
        booking.Start(TestData.Now.AddMinutes(80), procedureOnly).Error!.Code.ShouldBe(Required);
        booking.Start(TestData.Now.AddMinutes(80), TestData.Consents).IsSuccess.ShouldBeTrue();
        booking.AnaesthesiaConsentId.ShouldNotBeNull();
    }

    [Fact]
    public void A_postponement_starts_the_days_checks_afresh_with_the_technique_planned_then()
    {
        var booking = TestData.ReadyForSignIn();
        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), TestData.Consents).IsSuccess.ShouldBeTrue();

        booking.Postpone("Patient febrile").IsSuccess.ShouldBeTrue();
        booking.AnaesthesiaType = AnaesthesiaType.Local;

        booking.AnaesthesiaConsentRequiredAtSignIn.ShouldBeFalse();
        booking.Consent([TestData.Consent(SurgicalConsentKind.Procedure)]).Checks.Single(c => c.Kind == SurgicalConsentKind.Anaesthesia).State
            .ShouldBe(SurgicalConsentState.NotNeeded);
    }

    [Fact]
    public void A_sign_in_under_local_anaesthesia_asks_for_no_consent_to_anaesthesia()
    {
        var booking = TestData.NewBooking(AnaesthesiaType.Local);
        booking.Schedule(Guid.CreateVersion7(), TestData.Now.AddHours(1), TestData.Now.AddHours(2), TestData.Now).IsSuccess.ShouldBeTrue();
        IReadOnlyCollection<ConsentOnRecord> procedureOnly = [TestData.Consent(SurgicalConsentKind.Procedure)];
        booking.RecordPreOp(TestData.AllDone(PreOpChecklist.Items), TestData.Staff(), procedureOnly).IsSuccess.ShouldBeTrue();

        booking.ConfirmSafety(SafetyPhase.SignIn, Phase(SafetyPhase.SignIn), TestData.Staff(), procedureOnly).IsSuccess.ShouldBeTrue();

        booking.AnaesthesiaConsentRequiredAtSignIn.ShouldBeFalse();
        booking.ConsentsNeeded.ShouldBe([SurgicalConsentKind.Procedure]);
    }
}
