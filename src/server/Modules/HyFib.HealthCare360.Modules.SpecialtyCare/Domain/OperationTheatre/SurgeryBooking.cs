using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>
/// A surgery from request to completion (OT-01..03). The booking owns the pre-op and WHO checklists so that the
/// safety gates — no incision before Sign-in and Time-out, no completion before Sign-out, and no "consent signed", Sign-in,
/// Time-out or incision without the patient's recorded consent (<see cref="SurgicalConsent"/>) — are enforced by the
/// aggregate itself and cannot be bypassed by any endpoint.
/// </summary>
public sealed class SurgeryBooking : AuditableEntity, IFacilityScoped
{
    /// <summary>How far in the past a slot may start: emergency cases are often booked after the patient is wheeled in.</summary>
    public static readonly TimeSpan BackdatingGrace = TimeSpan.FromHours(1);

    public Guid FacilityId { get; set; }
    public string BookingNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? VisitId { get; set; }

    /// <summary>EMR order line this booking came from (null when booked at the OT desk).</summary>
    public Guid? OrderItemId { get; set; }

    public Guid? ProcedureServiceItemId { get; set; }
    public string? ProcedureCode { get; set; }
    public string ProcedureName { get; set; } = string.Empty;
    public Laterality Laterality { get; set; }

    /// <summary>The level or digit operated on, where a side alone does not say where (a spinal level, a finger, a tooth).</summary>
    public string? SiteLevel { get; set; }

    public string? Diagnosis { get; set; }

    public Guid? SurgeonId { get; set; }
    public List<Guid> AssistantIds { get; set; } = [];
    public Guid? AnaesthetistId { get; set; }

    /// <summary>
    /// The assisting (scrub) nurse or OT in-charge for the case: a nurse or OT technician from the practitioner master, so the
    /// OT register names them with their qualification and registration.
    /// </summary>
    public Guid? NurseId { get; set; }

    public AnaesthesiaType AnaesthesiaType { get; set; }
    public SurgeryPriority Priority { get; set; }

    /// <summary>Date the surgeon would like (requests only; the scheduled slot is authoritative once set).</summary>
    public DateOnly? PreferredDate { get; set; }

    public Guid? TheatreId { get; set; }
    public DateTimeOffset? ScheduledStart { get; set; }
    public DateTimeOffset? ScheduledEnd { get; set; }
    public SurgeryStatus Status { get; set; }

    public DateTimeOffset? ReceivedInPreOpAt { get; set; }

    /// <summary>Wheels-in. WHO Sign-in happens with the patient on the table before induction, so its completion marks it.</summary>
    public DateTimeOffset? InRoomAt { get; set; }

    /// <summary>Knife to skin.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Patient left the theatre.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The patient left the recovery room (<see cref="RecoveryRecord"/>), for <see cref="RecoveryDestination"/>. Until then,
    /// from the WHO Sign-out, the patient is in recovery: not back on the ward.
    /// </summary>
    public DateTimeOffset? RecoveryDischargedAt { get; set; }

    public RecoveryDestination? RecoveryDestination { get; set; }

    public List<ChecklistEntry> PreOpChecks { get; set; } = PreOpChecklist.NewEntries();
    public List<SafetyChecklistEntry> SafetyChecks { get; set; } = SurgicalSafetyChecklist.NewEntries();
    public DateTimeOffset? SignInCompletedAt { get; set; }
    public DateTimeOffset? TimeOutCompletedAt { get; set; }
    public DateTimeOffset? SignOutCompletedAt { get; set; }

    /// <summary>The swab, needle and instrument counts, each made by two members of the team.</summary>
    public List<SurgicalCount> Counts { get; set; } = [];

    /// <summary>Final counts that did not tally, and how each was settled.</summary>
    public List<CountDiscrepancy> CountDiscrepancies { get; set; } = [];

    /// <summary>WHO checklist phases voided by a change of side or procedure after the Sign-in, with who changed it and why.</summary>
    public List<ChecklistVoid> ChecklistVoids { get; set; } = [];

    /// <summary>The patient's consent to the procedure (front office, high-risk procedure) the incision relied on.</summary>
    public Guid? ProcedureConsentId { get; set; }

    /// <summary>The patient's consent to anaesthesia the incision relied on; none for local or topical anaesthesia.</summary>
    public Guid? AnaesthesiaConsentId { get; set; }

    /// <summary>When the operating surgeon waived consent in an emergency (<see cref="ConsentWaiver"/>).</summary>
    public DateTimeOffset? ConsentWaivedAt { get; set; }

    public Guid? ConsentWaivedByUserId { get; set; }
    public string? ConsentWaivedByName { get; set; }

    /// <summary>The second doctor who agreed, confirming at the surgeon's screen with their own credentials.</summary>
    public Guid? ConsentWaiverConcurredByUserId { get; set; }

    public string? ConsentWaiverConcurredByName { get; set; }

    /// <summary>The clinical reason the operation could not wait for consent.</summary>
    public string? ConsentWaiverReason { get; set; }

    /// <summary>
    /// A WHO Sign-in of this booking was completed under anaesthesia given by an anaesthetist, when the patient's consent to
    /// anaesthesia was needed: it stays needed though the technique is changed to local or topical after it
    /// (<see cref="ConsentsNeeded"/>). Cleared by a postponement, with the day's checks.
    /// </summary>
    public bool AnaesthesiaConsentRequiredAtSignIn { get; set; }

    public string? Notes { get; set; }
    public string? CancelReason { get; set; }
    public string? PostponeReason { get; set; }
    public int PostponeCount { get; set; }

    public ChargeStatus ChargeStatus { get; set; }
    public string? ChargeError { get; set; }

    /// <summary>Local and topical anaesthesia are given by the surgeon; every other technique needs a named anaesthetist.</summary>
    public bool RequiresAnaesthetist => !AnaesthesiaTypes.GivenBySurgeon(AnaesthesiaType);

    public bool IsClosed => Status is SurgeryStatus.Completed or SurgeryStatus.Cancelled;

    /// <summary>
    /// Between the WHO Sign-out and the discharge from recovery: the theatre still has the patient, in the recovery room
    /// (or on the way to it), and the ward sees them as returning only once they are discharged from it.
    /// </summary>
    public bool InRecovery => SignOutCompletedAt is not null && RecoveryDischargedAt is null && Status is SurgeryStatus.InProgress or SurgeryStatus.Completed;

    /// <summary>The procedure and where on the patient, as the WHO checklist confirms it.</summary>
    public SurgicalSite Site => new(ProcedureServiceItemId, ProcedureName, Laterality, SiteLevel);

    /// <summary>Consent waived in an emergency by the operating surgeon, with the second doctor who agreed, if it was.</summary>
    public SurgicalConsentWaiver? ConsentWaiver => ConsentWaivedAt is { } at
        ? new SurgicalConsentWaiver(at, ConsentWaivedByUserId, ConsentWaivedByName ?? string.Empty, ConsentWaiverConcurredByUserId,
            ConsentWaiverConcurredByName ?? string.Empty, ConsentWaiverReason ?? string.Empty)
        : null;

    /// <summary>
    /// The consents this operation needs: to the procedure, and to anaesthesia unless the surgeon gives it locally or
    /// topically, or when it was needed at the WHO Sign-in (<see cref="AnaesthesiaConsentRequiredAtSignIn"/>).
    /// </summary>
    public IReadOnlyList<SurgicalConsentKind> ConsentsNeeded => AnaesthesiaConsentRequiredAtSignIn
        ? [SurgicalConsentKind.Procedure, SurgicalConsentKind.Anaesthesia]
        : SurgicalConsent.Needed(AnaesthesiaType);

    /// <summary>
    /// Where the patient's consent to this operation stands, from <paramref name="consents"/> (the patient's consents on
    /// record): each counts from the booking (<see cref="AuditableEntity.CreatedAt"/>). A waiver stands only while the
    /// booking is an emergency, one no longer marked so goes ahead on the patient's consent, and never once the patient has
    /// said no, by a refusal or a withdrawal, whichever was recorded first (<see cref="SurgicalConsentStatus.WaiverStands"/>).
    /// </summary>
    public SurgicalConsentStatus Consent(IEnumerable<ConsentOnRecord> consents) =>
        SurgicalConsent.Assess(CreatedAt, ConsentsNeeded, consents, Priority == SurgeryPriority.Emergency ? ConsentWaiver : null);

    /// <summary>
    /// The operation goes ahead, or went ahead, on consent waived in an emergency rather than on the patient's consent. Once
    /// the incision is made: it relied on no consent of a kind it needed (<see cref="ProcedureConsentId"/>, or
    /// <see cref="AnaesthesiaConsentId"/> when the consent to anaesthesia was needed), under a waiver. Before it: the gates
    /// stand on the waiver (<see cref="SurgicalConsentStatus.OnWaiver"/>): it stands, and a consent needed is not recorded.
    /// An operation started before the theatre read consents relied on none, and on no waiver either.
    /// </summary>
    /// <param name="consents">The patient's consents on record; only read before the incision.</param>
    public bool ReliesOnWaiver(IEnumerable<ConsentOnRecord> consents) => StartedAt is not null
        ? ConsentWaivedAt is not null
          && ConsentsNeeded.Any(kind => (kind == SurgicalConsentKind.Procedure ? ProcedureConsentId : AnaesthesiaConsentId) is null)
        : Consent(consents).OnWaiver;

    /// <summary>
    /// Implants and consumables are corrected, not removed, once the operation has started (maker-checker slice 10): they have
    /// been used, charged and taken from stock, and an implant is in the patient.
    /// </summary>
    public bool MaterialsLocked => StartedAt is not null || SignOutCompletedAt is not null || Status is SurgeryStatus.InProgress or SurgeryStatus.Completed;

    /// <summary>Minutes the patient occupied the theatre (wheels-in to wheels-out), used for OT charges and utilisation.</summary>
    public double? MinutesInTheatre => CompletedAt is { } end && (InRoomAt ?? StartedAt) is { } start ? (end - start).TotalMinutes : null;

    public bool IsPhaseComplete(SafetyPhase phase) => phase switch
    {
        SafetyPhase.SignIn => SignInCompletedAt is not null,
        SafetyPhase.TimeOut => TimeOutCompletedAt is not null,
        _ => SignOutCompletedAt is not null,
    };

    public Result CanChangePlan() =>
        Status is SurgeryStatus.Requested or SurgeryStatus.Scheduled or SurgeryStatus.InPreOp or SurgeryStatus.Postponed
            ? Result.Success()
            : Error.Rule("surgery.plan_locked", "The surgical plan cannot be changed once surgery has started or the booking is closed.");

    /// <summary>
    /// Gives the booking a theatre and slot (or moves it). Conflict checks are done by the caller under a lock. Moving a
    /// booking already scheduled to another theatre or start ends a consent waived in an emergency, as a postponement does:
    /// the consent is taken for the new slot, or waived again if the operation still cannot wait.
    /// </summary>
    public Result Schedule(Guid theatreId, DateTimeOffset start, DateTimeOffset end, DateTimeOffset now)
    {
        if (Status is not (SurgeryStatus.Requested or SurgeryStatus.Scheduled or SurgeryStatus.Postponed))
        {
            return Error.Rule("surgery.not_schedulable", "Only requested, scheduled or postponed surgeries can be (re)scheduled.");
        }

        var window = SurgeryScheduling.ValidateWindow(start, end);
        if (window.IsFailure)
        {
            return window;
        }

        if (start < now - BackdatingGrace)
        {
            return Error.Field("scheduledStart", "The start time is in the past.");
        }

        if (SurgeonId is null)
        {
            return Error.Field("surgeonId", "Choose the operating surgeon before scheduling.");
        }

        if (RequiresAnaesthetist && AnaesthetistId is null)
        {
            return Error.Field("anaesthetistId", "Choose the anaesthetist (required for every technique except local and topical anaesthesia).");
        }

        if (Status == SurgeryStatus.Scheduled && (TheatreId != theatreId || ScheduledStart != start))
        {
            EndConsentWaiver();
        }

        TheatreId = theatreId;
        ScheduledStart = start;
        ScheduledEnd = end;
        Status = SurgeryStatus.Scheduled;
        return Result.Success();
    }

    /// <summary>
    /// Names the operating surgeon. A consent waived in an emergency was the operating surgeon's judgement, so it ends when
    /// another surgeon takes the case, who waives it again, with a second doctor, if the operation still cannot wait.
    /// </summary>
    public void AssignSurgeon(Guid? surgeonId)
    {
        if (surgeonId != SurgeonId)
        {
            EndConsentWaiver();
        }

        SurgeonId = surgeonId;
    }

    public Result ReceiveInPreOp(DateTimeOffset now)
    {
        if (Status != SurgeryStatus.Scheduled)
        {
            return Error.Rule("surgery.not_scheduled", "Only scheduled surgeries can be received in pre-op.");
        }

        Status = SurgeryStatus.InPreOp;
        ReceivedInPreOpAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Records pre-operative checklist answers. "Consent signed" (<see cref="PreOpChecklist.ConsentItem"/>) is answered (with
    /// anything but pending) only over the patient's consent to this operation as the front office recorded it, or consent
    /// waived in an emergency (<see cref="SurgicalConsent"/>): never on a tick alone.
    /// </summary>
    /// <param name="consents">The patient's consents on record, read as the checklist is saved.</param>
    /// <param name="canMarkSite">
    /// The signed-in user is a surgeon (holds the permission to operate), who may record that they marked the site.
    /// </param>
    public Result RecordPreOp(IReadOnlyList<ChecklistInput> items, StaffStamp by, IReadOnlyCollection<ConsentOnRecord> consents, bool canMarkSite = false)
    {
        if (Status is not (SurgeryStatus.Requested or SurgeryStatus.Scheduled or SurgeryStatus.InPreOp or SurgeryStatus.Postponed))
        {
            return Error.Rule("surgery.preop_closed", "The pre-operative checklist can no longer be changed for this booking.");
        }

        if (SignInCompletedAt is not null)
        {
            return Error.Rule("surgery.preop_locked", "The pre-operative checklist is locked after the WHO Sign-in.");
        }

        if (items.Any(i => i.Key == PreOpChecklist.ConsentItem && i.State != ChecklistItemState.Pending))
        {
            var consented = SurgicalConsent.Require(Consent(consents));
            if (consented.IsFailure)
            {
                return consented;
            }
        }

        var marking = SiteMarking.CheckPreOp(Site, PreOpChecks, items, canMarkSite);
        if (marking.IsFailure)
        {
            return marking;
        }

        return PreOpChecklist.Apply(PreOpChecks, SiteMarking.KeepMark(PreOpChecks, items), by);
    }

    /// <summary>
    /// Records WHO checklist items for one phase, enforcing the phase order and the surgery state. The Sign-in (whose first
    /// item confirms consent with the patient) and the Time-out are recorded only while the patient's consent to this
    /// operation is on record, or was waived in an emergency (<see cref="SurgicalConsent"/>), read as they are saved. A
    /// Sign-in completed under anaesthesia given by an anaesthetist keeps the consent to anaesthesia needed
    /// (<see cref="AnaesthesiaConsentRequiredAtSignIn"/>).
    /// </summary>
    /// <param name="consents">The patient's consents on record, read as the phase is saved.</param>
    public Result ConfirmSafety(SafetyPhase phase, IReadOnlyList<ChecklistInput> items, StaffStamp by, IReadOnlyCollection<ConsentOnRecord> consents)
    {
        if (IsPhaseComplete(phase))
        {
            return Error.Rule("surgery.phase_locked", $"{Describe(phase)} is already complete and locked.");
        }

        if (SurgicalSafetyChecklist.Prerequisite(phase) is { } before && !IsPhaseComplete(before))
        {
            return Error.Rule("surgery.phase_order", $"Complete the {Describe(before)} before the {Describe(phase)}.");
        }

        if (phase == SafetyPhase.SignOut)
        {
            if (Status != SurgeryStatus.InProgress)
            {
                return Error.Rule("surgery.not_in_progress", "The Sign-out is done at the end of surgery, after it has started.");
            }
        }
        else if (Status is not (SurgeryStatus.Scheduled or SurgeryStatus.InPreOp))
        {
            return Error.Rule("surgery.not_ready", $"The {Describe(phase)} is done on the day of surgery for a scheduled patient.");
        }

        if (phase == SafetyPhase.SignIn && !PreOpChecklist.IsComplete(PreOpChecks))
        {
            return Error.Rule("surgery.preop_incomplete", "Complete the pre-operative checklist before the WHO Sign-in.");
        }

        if (phase is SafetyPhase.SignIn or SafetyPhase.TimeOut)
        {
            var consented = SurgicalConsent.Require(Consent(consents));
            if (consented.IsFailure)
            {
                return consented;
            }
        }

        if (phase == SafetyPhase.SignIn)
        {
            var marked = SiteMarking.CheckSignIn(Site, PreOpChecks, items);
            if (marked.IsFailure)
            {
                return marked;
            }
        }

        if (phase == SafetyPhase.SignOut)
        {
            var counted = SurgicalCounts.CanConfirm(
                items.FirstOrDefault(i => i.Key == SurgicalSafetyChecklist.CountsItem)?.State ?? ChecklistItemState.Pending, Counts, CountDiscrepancies);
            if (counted.IsFailure)
            {
                return counted;
            }
        }

        var result = SurgicalSafetyChecklist.Apply(SafetyChecks, phase, items, by);
        if (result.IsFailure || !SurgicalSafetyChecklist.IsPhaseComplete(SafetyChecks, phase))
        {
            return result;
        }

        switch (phase)
        {
            case SafetyPhase.SignIn:
                SignInCompletedAt = by.At;
                InRoomAt ??= by.At;
                AnaesthesiaConsentRequiredAtSignIn |= SurgicalConsent.Needed(AnaesthesiaType).Contains(SurgicalConsentKind.Anaesthesia);
                break;
            case SafetyPhase.TimeOut:
                TimeOutCompletedAt = by.At;
                break;
            default:
                SignOutCompletedAt = by.At;
                break;
        }

        return Result.Success();
    }

    /// <summary>
    /// Knife to skin. WHO: never before the Sign-in and the Time-out are complete. NABH: never without the patient's consent
    /// to the operation (and to the anaesthesia, unless the surgeon gives it locally) on record, read as the incision is
    /// saved, or waived in an emergency (<see cref="SurgicalConsent"/>); the consents relied on are kept on the booking.
    /// </summary>
    /// <param name="consents">The patient's consents on record, read as the incision is saved.</param>
    public Result Start(DateTimeOffset now, IReadOnlyCollection<ConsentOnRecord> consents)
    {
        if (Status is not (SurgeryStatus.Scheduled or SurgeryStatus.InPreOp))
        {
            return Error.Rule("surgery.not_ready", "Only a scheduled surgery can be started.");
        }

        if (SignInCompletedAt is null || TimeOutCompletedAt is null)
        {
            return Error.Rule("surgery.checklist_incomplete", "Surgery cannot start until the WHO Sign-in and Time-out are complete.");
        }

        var consent = Consent(consents);
        var consented = SurgicalConsent.Require(consent);
        if (consented.IsFailure)
        {
            return consented;
        }

        Status = SurgeryStatus.InProgress;
        StartedAt = now;
        ProcedureConsentId = consent.ConsentIdOf(SurgicalConsentKind.Procedure);
        AnaesthesiaConsentId = consent.ConsentIdOf(SurgicalConsentKind.Anaesthesia);
        return Result.Success();
    }

    /// <summary>
    /// The emergency doctrine: an emergency operation that cannot wait goes ahead without the patient's consent. Only before
    /// the incision, for a booking marked an emergency, once, and while the consent is not on record; never when the
    /// patient's latest word since the booking on a consent the operation needs is "no", a refusal or a withdrawal (the
    /// doctrine is for a patient who cannot consent: a change of mind is recorded as a new consent at the front office).
    /// Waived by the operating surgeon from their own sign-in (<paramref name="surgeonPractitionerId"/> is the case's
    /// surgeon), with the clinical reason (at least <see cref="SurgicalConsent.MinimumReasonLength"/> characters), and a
    /// second doctor who agrees: a doctor on record, never the surgeon. The consent gates then stand on the waiver
    /// (<see cref="ConsentWaiver"/>) while it stands.
    /// </summary>
    public Result WaiveConsent(string? reason, StaffStamp surgeon, Guid? surgeonPractitionerId, ConcurringDoctor concurring, IReadOnlyCollection<ConsentOnRecord> consents)
    {
        ArgumentNullException.ThrowIfNull(surgeon);
        ArgumentNullException.ThrowIfNull(concurring);
        if (Status is not (SurgeryStatus.Requested or SurgeryStatus.Scheduled or SurgeryStatus.InPreOp))
        {
            return Error.Rule("surgery.consent_waiver_closed",
                "Consent is waived only before the operation starts, for a booking that is neither closed nor postponed.");
        }

        if (Priority != SurgeryPriority.Emergency)
        {
            return Error.Rule("surgery.consent_waiver_not_emergency",
                "Only an emergency operation goes ahead without the patient's consent (the emergency doctrine). Record the consent, or mark the booking an emergency if it is one.");
        }

        if (ConsentWaiver is { } waived)
        {
            return Error.Rule("surgery.consent_waived", $"Consent was already waived for this operation by {waived.SurgeonName}, {waived.ConcurringName} agreeing.");
        }

        var consent = Consent(consents);
        if (consent.Recorded)
        {
            return Error.Rule("surgery.consent_waiver_not_needed", "The patient's consent to this operation is recorded: there is nothing to waive.");
        }

        if (consent.No is { } no)
        {
            return no.State == SurgicalConsentState.Refused
                ? Error.Rule(SurgicalConsent.RefusedCode,
                    $"The patient refused {SurgicalConsent.What(no.Kind)} after this operation was booked, and an emergency does not override a refusal: "
                    + "the emergency doctrine is for a patient who cannot consent. If the patient now agrees, record the new consent under the patient's consents (front office).")
                : Error.Rule(SurgicalConsent.WithdrawnCode,
                    $"The patient withdrew {SurgicalConsent.What(no.Kind)} after this operation was booked, and an emergency does not override a withdrawal: "
                    + "the emergency doctrine is for a patient who cannot consent. If the patient now agrees, record the new consent under the patient's consents (front office).");
        }

        if (surgeonPractitionerId is not { } by || by != SurgeonId)
        {
            return Error.Forbidden("surgery.consent_waiver_surgeon", "Only the operating surgeon of this case waives consent, from their own sign-in.");
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < SurgicalConsent.MinimumReasonLength)
        {
            return Error.Field("reason", "Give the clinical reason the operation cannot wait for the patient's consent (at least 10 characters).");
        }

        if (concurring.UserId == surgeon.UserId || (concurring.PractitionerId is { } second && second == SurgeonId))
        {
            return Error.Rule("surgery.consent_waiver_same_doctor", "A second doctor, not the operating surgeon, agrees that the operation cannot wait.");
        }

        if (!concurring.IsDoctor)
        {
            return Error.Rule("surgery.consent_waiver_not_doctor",
                $"{concurring.Name} is not a doctor on record: the doctor who agrees has an account linked to an active doctor's practitioner record.");
        }

        ConsentWaivedAt = surgeon.At;
        (ConsentWaivedByUserId, ConsentWaivedByName) = (surgeon.UserId, surgeon.Name);
        (ConsentWaiverConcurredByUserId, ConsentWaiverConcurredByName) = (concurring.UserId, concurring.Name);
        ConsentWaiverReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>
    /// Patient leaves the theatre. WHO: never before the Sign-out is complete. Announces the operation so that it is
    /// recorded as a procedure on the patient's record (EHR-01).
    /// </summary>
    public Result Complete(DateTimeOffset now)
    {
        if (Status != SurgeryStatus.InProgress)
        {
            return Error.Rule("surgery.not_in_progress", "Only a surgery in progress can be completed.");
        }

        if (SignOutCompletedAt is null)
        {
            return Error.Rule("surgery.signout_incomplete", "Surgery cannot be completed until the WHO Sign-out is complete.");
        }

        Status = SurgeryStatus.Completed;
        CompletedAt = now;
        Raise(new SurgeryCompleted(Id, BookingNumber, FacilityId, PatientId, AdmissionId, VisitId, ProcedureCode, ProcedureName, Side(Laterality),
            SurgeonId, StartedAt, now));
        return Result.Success();
    }

    /// <summary>
    /// Changes the side or procedure after the WHO Sign-in (checked by <see cref="SiteChangeRule"/> before the plan is
    /// applied): the Sign-in, and the Time-out if done, no longer stand, because what they confirmed has changed. Their
    /// confirmations are kept in <see cref="ChecklistVoids"/> and the phases are redone. A mark on the old side or level is no
    /// longer the mark, so the pre-op site marking is redone too.
    /// </summary>
    public ChecklistVoid VoidChecklistForSiteChange(SurgicalSite before, string reason, StaffStamp by)
    {
        ArgumentNullException.ThrowIfNull(before);
        var phases = new[] { SafetyPhase.SignIn, SafetyPhase.TimeOut, SafetyPhase.SignOut }.Where(IsPhaseComplete).ToList();
        var voided = SafetyChecks.Where(e => e.State != ChecklistItemState.Pending).ToList();
        var record = new ChecklistVoid(by.At, by.UserId, by.Name, reason.Trim(), before.Words, Site.Words, phases, voided);
        ChecklistVoids.Add(record);
        SafetyChecks = SurgicalSafetyChecklist.NewEntries();
        SignInCompletedAt = TimeOutCompletedAt = SignOutCompletedAt = null;
        return record;
    }

    /// <summary>
    /// After any change of side or level, a site mark already recorded (or recorded as not applicable) no longer stands: it
    /// marked another place. Called after the plan is applied.
    /// </summary>
    public void ResetSiteMarkingIfMoved(SurgicalSite before)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (!before.PlaceDiffers(Site))
        {
            return;
        }

        var index = PreOpChecks.FindIndex(e => e.Key == SiteMarking.Item);
        if (index >= 0 && PreOpChecks[index].State != ChecklistItemState.Pending)
        {
            PreOpChecks[index] = new ChecklistEntry(SiteMarking.Item, ChecklistItemState.Pending, null, null, null, null);
        }
    }

    /// <summary>Records one count, witnessed; a final count that does not tally opens a discrepancy.</summary>
    public Result<CountDiscrepancy?> RecordCount(SurgicalCount count)
    {
        ArgumentNullException.ThrowIfNull(count);
        var allowed = SurgicalCounts.CanRecord(Status, SignOutCompletedAt is not null, Counts, count.Category, count.Stage, count.Number);
        if (allowed.IsFailure)
        {
            return allowed.Error!;
        }

        Counts.Add(count);
        if (count.Stage != CountStage.Final)
        {
            return (CountDiscrepancy?)null;
        }

        var tally = SurgicalCounts.Tally(Counts).Single(t => t.Category == count.Category);
        if (tally.Tallies)
        {
            return (CountDiscrepancy?)null;
        }

        var discrepancy = new CountDiscrepancy(Guid.CreateVersion7(), count.Category, tally.Expected ?? 0, count.Number, count.Id, count.At);
        CountDiscrepancies.Add(discrepancy);
        return discrepancy;
    }

    /// <summary>
    /// Settles an open discrepancy. Found needs where; an X-ray needs its result; a retained item needs the incident it was
    /// reported as (raised by the caller in the Quality register before this is saved).
    /// </summary>
    public Result<CountDiscrepancy> ResolveDiscrepancy(
        Guid discrepancyId, DiscrepancyResolution resolution, string note, string? xrayResult, (Guid Id, string Number)? incident, StaffStamp by)
    {
        var index = CountDiscrepancies.FindIndex(d => d.Id == discrepancyId);
        if (index < 0)
        {
            return Error.NotFound(nameof(CountDiscrepancy), discrepancyId);
        }

        var open = CountDiscrepancies[index];
        if (!open.IsOpen)
        {
            return Error.Rule("surgery.discrepancy_settled", $"The discrepancy in the {SurgicalCounts.Describe(open.Category)} count is already settled.");
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", resolution == DiscrepancyResolution.Found ? "Say where the item was found." : "Describe what was done.");
        }

        if (resolution == DiscrepancyResolution.XrayNoRetainedItem && string.IsNullOrWhiteSpace(xrayResult))
        {
            return Error.Field("xrayResult", "Enter the result of the X-ray.");
        }

        if (resolution == DiscrepancyResolution.RetainedItem && incident is null)
        {
            return Error.Rule("surgery.discrepancy_incident", "A retained item is reported in the incident register first.");
        }

        var settled = open with
        {
            Resolution = resolution,
            ResolutionNote = note.Trim(),
            XrayResult = string.IsNullOrWhiteSpace(xrayResult) ? null : xrayResult.Trim(),
            IncidentId = incident?.Id,
            IncidentNumber = incident?.Number,
            ResolvedByUserId = by.UserId,
            ResolvedByName = by.Name,
            ResolvedAt = by.At,
        };
        CountDiscrepancies[index] = settled;
        return settled;
    }

    /// <summary>
    /// Frees the slot. The WHO checklist is reset because its checks (identity, site, equipment) are only valid on
    /// the day they were done; the pre-op checklist keeps its timestamps so staff can judge what to re-verify. A consent
    /// waived in an emergency ends too: the operation waited, so the consent is taken for the new slot, or waived again if
    /// it still cannot wait (the waiver stays in the audit trail, <c>surgery.consent_waived</c>). The consent to
    /// anaesthesia kept needed by the day's Sign-in (<see cref="AnaesthesiaConsentRequiredAtSignIn"/>) goes with it.
    /// </summary>
    public Result Postpone(string reason)
    {
        if (Status is not (SurgeryStatus.Requested or SurgeryStatus.Scheduled or SurgeryStatus.InPreOp))
        {
            return Error.Rule("surgery.not_postponable", "Only surgeries that have not started can be postponed.");
        }

        Status = SurgeryStatus.Postponed;
        PostponeReason = reason.Trim();
        PostponeCount++;
        ReceivedInPreOpAt = null;
        InRoomAt = null;
        SafetyChecks = SurgicalSafetyChecklist.NewEntries();
        SignInCompletedAt = TimeOutCompletedAt = SignOutCompletedAt = null;
        AnaesthesiaConsentRequiredAtSignIn = false;
        EndConsentWaiver();
        return Result.Success();
    }

    /// <summary>
    /// A consent waived in an emergency no longer stands (the operation waited, or another surgeon took the case); the
    /// waiver stays in the audit trail (<c>surgery.consent_waived</c>) and in the booking's change history.
    /// </summary>
    private void EndConsentWaiver()
    {
        ConsentWaivedAt = null;
        (ConsentWaivedByUserId, ConsentWaivedByName, ConsentWaiverConcurredByUserId, ConsentWaiverConcurredByName, ConsentWaiverReason) =
            (null, null, null, null, null);
    }

    public Result Cancel(string reason)
    {
        if (Status is SurgeryStatus.InProgress or SurgeryStatus.Completed or SurgeryStatus.Cancelled)
        {
            return Error.Rule("surgery.not_cancellable", "A surgery that has started or is closed cannot be cancelled.");
        }

        Status = SurgeryStatus.Cancelled;
        CancelReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>The patient left the recovery room (the record's discharge, saved with it).</summary>
    public void DischargeFromRecovery(DateTimeOffset at, RecoveryDestination destination)
    {
        RecoveryDischargedAt = at;
        RecoveryDestination = destination;
    }

    public void RecordCharges(ChargeStatus status, string? error)
    {
        ChargeStatus = status;
        ChargeError = error;
    }

    public SurgerySlot? ToSlot() =>
        TheatreId is { } theatre && SurgeonId is { } surgeon && ScheduledStart is { } start && ScheduledEnd is { } end
            ? new SurgerySlot(Id, theatre, surgeon, PatientId, AnaesthetistId, start, end)
            : null;

    private static ProcedureSide Side(Laterality laterality) => laterality switch
    {
        Laterality.Left => ProcedureSide.Left,
        Laterality.Right => ProcedureSide.Right,
        Laterality.Bilateral => ProcedureSide.Bilateral,
        _ => ProcedureSide.NotApplicable,
    };

    public static string Describe(SafetyPhase phase) => phase switch
    {
        SafetyPhase.SignIn => "WHO Sign-in",
        SafetyPhase.TimeOut => "WHO Time-out",
        _ => "WHO Sign-out",
    };
}
