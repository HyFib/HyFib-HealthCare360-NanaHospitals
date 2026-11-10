namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum MedicationOrderSource
{
    /// <summary>Copied from a signed in-patient e-prescription.</summary>
    Prescription = 0,

    /// <summary>Issued by the IP pharmacy without a linked prescription line.</summary>
    Pharmacy,

    /// <summary>Transcribed on the ward (verbal or paper order).</summary>
    Manual,
}

public enum MedicationOrderStatus
{
    Active = 0,
    Stopped,
    Completed,
}

/// <summary>
/// How the order reached the chart. A doctor who writes an order in their own name signs it there and then (<see cref="Written"/>,
/// as is every e-prescription line); anyone else writing it in a doctor's name records what the doctor said, face to face
/// or on the telephone, read back to the doctor, and the doctor countersigns it from their own sign-in (NABH MOM).
/// </summary>
public enum MedicationOrderMode
{
    /// <summary>Signed by the prescriber who wrote it: an e-prescription line, or a doctor's own ward order.</summary>
    Written = 0,

    /// <summary>Given face to face and written by someone else, awaiting the doctor's countersignature.</summary>
    Verbal,

    /// <summary>Given on the telephone and written by someone else, awaiting the doctor's countersignature.</summary>
    Telephone,
}

/// <summary>
/// A line on the in-patient medication chart: what the doctor ordered. MAR rows are scheduled from its frequency when
/// the pharmacy dispenses it (or when a ward order is transcribed), so the chart shows only doses that are really
/// available on the ward.
/// </summary>
public sealed class MedicationOrder : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? PrescriptionId { get; set; }
    public Guid? PrescriptionLineId { get; set; }
    public Guid? DrugItemId { get; set; }
    public string DrugName { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? Strength { get; set; }
    public string Dose { get; set; } = string.Empty;
    public string? Route { get; set; }
    public string Frequency { get; set; } = string.Empty;
    public int? DurationDays { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset? EndAt { get; set; }
    public string? Instructions { get; set; }

    /// <summary>
    /// Schedule H1/X or narcotic: administration needs a witness (second nurse). From the drug master when the order names
    /// an item of it; an order typed in on the ward without one is handled as high-alert (<see cref="IsHighAlert"/>), so the
    /// flags cannot be cleared by typing a name instead of picking the drug.
    /// </summary>
    public bool IsControlledDrug { get; set; }

    /// <summary>
    /// A high-alert medicine (insulin, heparin, concentrated potassium chloride, chemotherapy…), from the drug master when the
    /// order names an item of it: administration needs a witness (second nurse), as a controlled drug's does. A ward order
    /// typed in without a drug-master item is always high-alert: what it is cannot be known, so it is handled as the worst.
    /// </summary>
    public bool IsHighAlert { get; set; }

    public MedicationOrderSource Source { get; set; }
    public MedicationOrderStatus Status { get; set; }

    /// <summary>The doctor in whose name the order is given: who countersigns it when someone else wrote it.</summary>
    public Guid? OrderedByPractitionerId { get; set; }

    public DateTimeOffset? LastDispensedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public Guid? StoppedBy { get; set; }
    public string? StopReason { get; set; }

    /// <summary>Written by the doctor named, or a verbal or telephone order someone else wrote in the doctor's name.</summary>
    public MedicationOrderMode Mode { get; set; }

    /// <summary>A verbal or telephone order was read back to the doctor, who confirmed it, before it was charted.</summary>
    public bool ReadBack { get; set; }

    /// <summary>
    /// A verbal order the nurse marked as an emergency (read back): a high-alert, controlled or chemotherapy medicine is
    /// given before the doctor countersigns it, each dose witnessed as always, and the order stays flagged until then.
    /// </summary>
    public bool IsEmergency { get; set; }

    /// <summary>Who wrote the order on the ward (the doctor named, or the nurse who took the verbal order).</summary>
    public Guid? EnteredBy { get; set; }

    public string? EnteredByName { get; set; }

    /// <summary>When a verbal or telephone order should be countersigned by (setting <c>inpatient.verbalOrderCountersignHours</c>).</summary>
    public DateTimeOffset? CountersignDueAt { get; set; }

    public DateTimeOffset? CountersignedAt { get; private set; }
    public Guid? CountersignedBy { get; private set; }
    public string? CountersignedByName { get; private set; }

    /// <summary>When the doctor and the ward's nurses in charge were told the order was not countersigned in time.</summary>
    public DateTimeOffset? CountersignOverdueNotifiedAt { get; set; }

    /// <summary>
    /// A stop asked for by someone who does not prescribe (a nurse): the doses are held while it waits for the doctor's
    /// countersignature, and the order stops once the doctor confirms it.
    /// </summary>
    public DateTimeOffset? StopRequestedAt { get; private set; }

    public Guid? StopRequestedBy { get; private set; }
    public string? StopRequestedByName { get; private set; }
    public string? StopRequestReason { get; private set; }
    public DateTimeOffset? StopCountersignDueAt { get; private set; }

    /// <summary>When the doctor and the ward's nurses in charge were told the stop was not countersigned in time.</summary>
    public DateTimeOffset? StopOverdueNotifiedAt { get; set; }

    public bool IsAsNeeded => DoseSchedule.IsAsNeeded(Frequency);

    /// <summary>A verbal or telephone order the doctor has not yet countersigned (stopped or not: the record still needs it).</summary>
    public bool AwaitingCountersignature => Mode != MedicationOrderMode.Written && CountersignedAt is null;

    /// <summary>A controlled drug or a high-alert medicine (chemotherapy among them, and any medicine typed in that the drug master does not name).</summary>
    public bool IsHighRisk => IsControlledDrug || IsHighAlert;

    /// <summary>A stop a nurse asked for, waiting for the doctor's countersignature.</summary>
    public bool StopPending => StopRequestedAt is not null && Status == MedicationOrderStatus.Active;

    /// <summary>No dose of this order is given now (<see cref="CanGiveDose"/> says why).</summary>
    public bool DosesHeld => CanGiveDose().IsFailure;

    public bool CountersignOverdue(DateTimeOffset now) => AwaitingCountersignature && CountersignDueAt is { } due && now > due;

    public bool StopCountersignOverdue(DateTimeOffset now) => StopPending && StopCountersignDueAt is { } due && now > due;

    /// <summary>
    /// Whether a dose may be given now. A stop waiting for the doctor holds every dose (the safe direction); so does a
    /// verbal or telephone order of a high-alert, controlled or chemotherapy medicine until the doctor countersigns it,
    /// unless the nurse took it as an emergency order, read back.
    /// </summary>
    public Result CanGiveDose()
    {
        if (StopPending)
        {
            return Error.Rule("medication.stop_pending",
                "A stop of this medicine is waiting for the doctor's countersignature, so its doses are held. Record a dose due now as held.");
        }

        if (AwaitingCountersignature && IsHighRisk && !IsEmergency)
        {
            return Error.Rule("medication.awaiting_countersignature",
                "This verbal or telephone order of a high-alert or controlled medicine gives no dose until the doctor countersigns it. In an emergency, write it as an emergency verbal order, read back.");
        }

        return Result.Success();
    }

    public Result Stop(Guid? userId, string reason, DateTimeOffset now)
    {
        if (Status != MedicationOrderStatus.Active)
        {
            return Error.Rule("medication.not_active", "This medication order is no longer active.");
        }

        Status = MedicationOrderStatus.Stopped;
        StoppedAt = now;
        StoppedBy = userId;
        StopReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>The doctor named countersigns the verbal or telephone order someone else wrote in their name.</summary>
    public Result Countersign(Guid userId, string name, DateTimeOffset now)
    {
        if (Mode == MedicationOrderMode.Written)
        {
            return Error.Rule("medication.not_verbal", "This order was written by the doctor and needs no countersignature.");
        }

        if (CountersignedAt is not null)
        {
            return Error.Rule("medication.already_countersigned", "This order is already countersigned.");
        }

        CountersignedAt = now;
        CountersignedBy = userId;
        CountersignedByName = name;
        return Result.Success();
    }

    /// <summary>A nurse asks for the order to stop: its doses are held until the doctor confirms the stop or declines it.</summary>
    public Result RequestStop(Guid userId, string name, string reason, DateTimeOffset now, DateTimeOffset countersignBy)
    {
        if (Status != MedicationOrderStatus.Active)
        {
            return Error.Rule("medication.not_active", "This medication order is no longer active.");
        }

        if (StopPending)
        {
            return Error.Rule("medication.stop_pending", "A stop of this medicine is already waiting for the doctor's countersignature.");
        }

        StopRequestedAt = now;
        StopRequestedBy = userId;
        StopRequestedByName = name;
        StopRequestReason = reason.Trim();
        StopCountersignDueAt = countersignBy;
        StopOverdueNotifiedAt = null;
        return Result.Success();
    }

    /// <summary>The doctor confirms the stop a nurse asked for: the order stops, in the doctor's name, for the nurse's reason.</summary>
    public Result ConfirmStop(Guid userId, DateTimeOffset now)
    {
        if (!StopPending)
        {
            return Error.Rule("medication.no_stop_pending", "No stop of this medicine is waiting for a countersignature.");
        }

        return Stop(userId, StopRequestReason ?? "Stopped", now);
    }

    /// <summary>The doctor declines the stop a nurse asked for: the order goes on, and its doses are given again.</summary>
    public Result DeclineStop()
    {
        if (!StopPending)
        {
            return Error.Rule("medication.no_stop_pending", "No stop of this medicine is waiting for a countersignature.");
        }

        (StopRequestedAt, StopRequestedBy, StopRequestedByName, StopRequestReason, StopCountersignDueAt, StopOverdueNotifiedAt) =
            (null, null, null, null, null, null);
        return Result.Success();
    }
}

public enum MarStatus
{
    Due = 0,
    Given,
    Held,
    Refused,
    Missed,

    /// <summary>The order was stopped before the dose was due.</summary>
    Cancelled,
}

/// <summary>One scheduled or ad-hoc (SOS/STAT) dose on the Medication Administration Record.</summary>
public sealed class MedicationAdministration : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? MedicationOrderId { get; set; }
    public Guid? DispenseId { get; set; }
    public string DrugName { get; set; } = string.Empty;
    public string Dose { get; set; } = string.Empty;
    public string? Route { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public MarStatus Status { get; set; }
    public bool IsAdHoc { get; set; }
    public DateTimeOffset? AdministeredAt { get; set; }
    public DateTimeOffset? RecordedAt { get; set; }
    public Guid? RecordedBy { get; set; }
    public string? RecordedByName { get; set; }
    public string? Reason { get; set; }
    public string? BatchNumber { get; set; }

    /// <summary>
    /// The second nurse who witnessed the dose, who proved who they are at the recording nurse's screen
    /// (<see cref="MarWitness"/>). Doses recorded before that was asked for name a witness by the name typed, with no account.
    /// </summary>
    public Guid? WitnessUserId { get; set; }

    public string? WitnessName { get; set; }
    public string? Notes { get; set; }

    public bool IsOverdue(DateTimeOffset now, TimeSpan grace) => MarRules.IsOverdue(Status, ScheduledAt, now, grace);

    public Result Record(MarOutcome outcome, Guid? userId, string? userName, bool requiresWitness, DateTimeOffset now)
    {
        var check = MarRules.ValidateOutcome(Status, outcome, requiresWitness, now);
        if (check.IsFailure)
        {
            return check;
        }

        Status = outcome.Status;
        AdministeredAt = outcome.Status == MarStatus.Given ? outcome.AdministeredAt ?? now : null;
        RecordedAt = now;
        RecordedBy = userId;
        RecordedByName = userName;
        Reason = Clean(outcome.Reason);
        BatchNumber = Clean(outcome.BatchNumber) ?? BatchNumber;
        (WitnessUserId, WitnessName) = (outcome.Witness?.UserId, outcome.Witness?.Name);
        Notes = Clean(outcome.Notes);
        return Result.Success();
    }

    public void CancelIfDue(string reason)
    {
        if (Status == MarStatus.Due)
        {
            Status = MarStatus.Cancelled;
            Reason = reason;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What the nurse recorded for a dose.</summary>
public sealed record MarOutcome(MarStatus Status, DateTimeOffset? AdministeredAt, string? Reason, string? BatchNumber, MarWitness? Witness, string? Notes);

/// <summary>A second nurse who witnessed a dose, and proved who they are at the recording nurse's screen: never that nurse.</summary>
public sealed record MarWitness(Guid UserId, string Name);

/// <summary>
/// MAR recording rules. A dose is recorded once (corrections are nursing notes, never overwrites); anything other than
/// "given" needs a reason; controlled drugs given need a witness who proved who they are; and administration cannot be
/// documented in the future.
/// </summary>
public static class MarRules
{
    /// <summary>Default window after the scheduled time before a due dose shows as overdue (setting <c>inpatient.marGraceMinutes</c>).</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    public static bool IsOverdue(MarStatus status, DateTimeOffset scheduledAt, DateTimeOffset now, TimeSpan grace) =>
        status == MarStatus.Due && now > scheduledAt + grace;

    /// <summary>
    /// Whether a dose given is witnessed by a second nurse: always for a dose with no order behind it (nobody prescribed it,
    /// so the second nurse is the only other check), and for an order of a controlled drug or a high-alert medicine.
    /// </summary>
    public static bool RequiresWitness(MedicationOrder? order) => order is null || order.IsHighRisk;

    public static Result ValidateOutcome(MarStatus current, MarOutcome outcome, bool requiresWitness, DateTimeOffset now)
    {
        if (current != MarStatus.Due)
        {
            return Error.Rule("mar.already_recorded", "This dose has already been recorded. Write a nursing note to correct it.");
        }

        if (outcome.Status is MarStatus.Due or MarStatus.Cancelled)
        {
            return Error.Rule("mar.invalid_outcome", "Record the dose as given, held, refused or missed.");
        }

        if (outcome.Status != MarStatus.Given && string.IsNullOrWhiteSpace(outcome.Reason))
        {
            return Error.Rule("mar.reason_required", "Give a reason when a dose is held, refused or missed.");
        }

        if (outcome.Status == MarStatus.Given && requiresWitness && outcome.Witness is null)
        {
            return Error.Rule("mar.witness_required",
                "Controlled drugs, high-alert medicines and doses with no order are witnessed: a second nurse confirms at this screen with their own user name and password.");
        }

        if (outcome.AdministeredAt is { } at && at > now + ClockTolerance)
        {
            return Error.Rule("mar.future", "The administration time cannot be in the future.");
        }

        return Result.Success();
    }
}
