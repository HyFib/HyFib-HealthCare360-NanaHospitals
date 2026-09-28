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

    /// <summary>Schedule H1/X or narcotic: administration needs a witness (second nurse).</summary>
    public bool IsControlledDrug { get; set; }

    public MedicationOrderSource Source { get; set; }
    public MedicationOrderStatus Status { get; set; }
    public Guid? OrderedByPractitionerId { get; set; }
    public DateTimeOffset? LastDispensedAt { get; set; }
    public DateTimeOffset? StoppedAt { get; set; }
    public Guid? StoppedBy { get; set; }
    public string? StopReason { get; set; }

    public bool IsAsNeeded => DoseSchedule.IsAsNeeded(Frequency);

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
        WitnessName = Clean(outcome.WitnessName);
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
public sealed record MarOutcome(MarStatus Status, DateTimeOffset? AdministeredAt, string? Reason, string? BatchNumber, string? WitnessName, string? Notes);

/// <summary>
/// MAR recording rules. A dose is recorded once (corrections are nursing notes, never overwrites); anything other than
/// "given" needs a reason; controlled drugs need a witness; and administration cannot be documented in the future.
/// </summary>
public static class MarRules
{
    /// <summary>Default window after the scheduled time before a due dose shows as overdue (setting <c>inpatient.marGraceMinutes</c>).</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    public static bool IsOverdue(MarStatus status, DateTimeOffset scheduledAt, DateTimeOffset now, TimeSpan grace) =>
        status == MarStatus.Due && now > scheduledAt + grace;

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

        if (outcome.Status == MarStatus.Given && requiresWitness && string.IsNullOrWhiteSpace(outcome.WitnessName))
        {
            return Error.Rule("mar.witness_required", "Controlled drugs must be witnessed. Enter the name of the witnessing nurse.");
        }

        if (outcome.AdministeredAt is { } at && at > now + ClockTolerance)
        {
            return Error.Rule("mar.future", "The administration time cannot be in the future.");
        }

        return Result.Success();
    }
}
