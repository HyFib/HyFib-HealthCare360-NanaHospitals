using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>
/// A surgery from request to completion (OT-01..03). The booking owns the pre-op and WHO checklists so that the
/// safety gates — no incision before Sign-in and Time-out, no completion before Sign-out — are enforced by the
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

    /// <summary>Gives the booking a theatre and slot (or moves it). Conflict checks are done by the caller under a lock.</summary>
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

        TheatreId = theatreId;
        ScheduledStart = start;
        ScheduledEnd = end;
        Status = SurgeryStatus.Scheduled;
        return Result.Success();
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

    /// <param name="canMarkSite">
    /// The signed-in user is a surgeon (holds the permission to operate), who may record that they marked the site.
    /// </param>
    public Result RecordPreOp(IReadOnlyList<ChecklistInput> items, StaffStamp by, bool canMarkSite = false)
    {
        if (Status is not (SurgeryStatus.Requested or SurgeryStatus.Scheduled or SurgeryStatus.InPreOp or SurgeryStatus.Postponed))
        {
            return Error.Rule("surgery.preop_closed", "The pre-operative checklist can no longer be changed for this booking.");
        }

        if (SignInCompletedAt is not null)
        {
            return Error.Rule("surgery.preop_locked", "The pre-operative checklist is locked after the WHO Sign-in.");
        }

        var marking = SiteMarking.CheckPreOp(Site, PreOpChecks, items, canMarkSite);
        if (marking.IsFailure)
        {
            return marking;
        }

        return PreOpChecklist.Apply(PreOpChecks, SiteMarking.KeepMark(PreOpChecks, items), by);
    }

    /// <summary>Records WHO checklist items for one phase, enforcing the phase order and the surgery state.</summary>
    public Result ConfirmSafety(SafetyPhase phase, IReadOnlyList<ChecklistInput> items, StaffStamp by)
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

    /// <summary>Knife to skin. WHO: never before the Sign-in and the Time-out are complete.</summary>
    public Result Start(DateTimeOffset now)
    {
        if (Status is not (SurgeryStatus.Scheduled or SurgeryStatus.InPreOp))
        {
            return Error.Rule("surgery.not_ready", "Only a scheduled surgery can be started.");
        }

        if (SignInCompletedAt is null || TimeOutCompletedAt is null)
        {
            return Error.Rule("surgery.checklist_incomplete", "Surgery cannot start until the WHO Sign-in and Time-out are complete.");
        }

        Status = SurgeryStatus.InProgress;
        StartedAt = now;
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
    /// the day they were done; the pre-op checklist keeps its timestamps so staff can judge what to re-verify.
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
        return Result.Success();
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
