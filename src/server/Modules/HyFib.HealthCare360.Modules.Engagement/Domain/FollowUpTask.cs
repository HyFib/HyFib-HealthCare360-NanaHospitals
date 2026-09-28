namespace HyFib.HealthCare360.Modules.Engagement.Domain;

public enum FollowUpType
{
    PostConsultation = 0,
    PostDischarge,
    Vaccination,
    ChronicReview,
    HealthCheckupDue,
    PaymentDue,
    Feedback,
}

public enum FollowUpStatus
{
    Open = 0,
    InProgress,
    Completed,
    Cancelled,
}

public enum FollowUpPriority
{
    Normal = 0,
    High,
}

public enum FollowUpOutcome
{
    Reached = 0,
    AppointmentBooked,
    NotInterested,
    WrongNumber,
    Deceased,
    NotReachable,
    CallBackLater,
    Other,
}

public enum FollowUpSource
{
    Manual = 0,
    Discharge,
    System,
}

/// <summary>
/// A call-back task for the CRM team (CRM-02): post-discharge check, vaccination or chronic-disease review, health
/// check-up due… Unanswered calls are retried the next day; after <see cref="MaxAttempts"/> unanswered attempts the
/// task closes as not reachable so worklists do not fill up with dead numbers.
/// </summary>
public sealed class FollowUpTask : AuditableEntity, IFacilityScoped
{
    public const int MaxAttempts = 3;

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public FollowUpType Type { get; set; }
    public DateOnly DueDate { get; set; }
    public Guid? AssigneeUserId { get; set; }
    public FollowUpStatus Status { get; set; }
    public FollowUpPriority Priority { get; set; }
    public string? Notes { get; set; }
    public FollowUpOutcome? Outcome { get; set; }
    public string? OutcomeNotes { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CancelReason { get; set; }
    public FollowUpSource Source { get; set; }

    /// <summary>Idempotency key for automatic tasks (for example "discharge:{admissionId}").</summary>
    public string? SourceReference { get; set; }

    public bool IsOpen => Status is FollowUpStatus.Open or FollowUpStatus.InProgress;

    public bool IsOverdue(DateOnly today) => IsOpen && DueDate < today;

    public static bool IsRetryOutcome(FollowUpOutcome outcome) => outcome is FollowUpOutcome.NotReachable or FollowUpOutcome.CallBackLater;

    /// <summary>
    /// Records a call attempt. Final outcomes close the task; "not reachable" and "call back later" keep it open with a
    /// new due date (default tomorrow) until the attempt limit is reached.
    /// </summary>
    public Result RecordAttempt(FollowUpOutcome outcome, string? notes, DateOnly? nextDueDate, DateOnly today, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("followup.closed", "This follow-up is already closed.");
        }

        if (nextDueDate is { } next && next < today)
        {
            return Error.Field("nextDueDate", "The next call date cannot be in the past.");
        }

        Attempts++;
        Outcome = outcome;
        OutcomeNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        if (IsRetryOutcome(outcome) && !(outcome == FollowUpOutcome.NotReachable && Attempts >= MaxAttempts))
        {
            Status = FollowUpStatus.InProgress;
            DueDate = nextDueDate ?? today.AddDays(1);
            return Result.Success();
        }

        Status = FollowUpStatus.Completed;
        CompletedAt = now;
        return Result.Success();
    }

    /// <summary>"Come back" tasks whose purpose is fulfilled once the patient books; the others still need the call.</summary>
    public static bool ClosesOnBooking(FollowUpType type) =>
        type is FollowUpType.PostConsultation or FollowUpType.Vaccination or FollowUpType.ChronicReview or FollowUpType.HealthCheckupDue;

    /// <summary>Completes the task because the patient booked an appointment (not counted as a call attempt).</summary>
    public Result CompleteAsBooked(string note, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("followup.closed", "This follow-up is already closed.");
        }

        Status = FollowUpStatus.Completed;
        Outcome = FollowUpOutcome.AppointmentBooked;
        OutcomeNotes = note.Trim();
        CompletedAt = now;
        return Result.Success();
    }

    public Result Cancel(string reason)
    {
        if (!IsOpen)
        {
            return Error.Rule("followup.closed", "This follow-up is already closed.");
        }

        Status = FollowUpStatus.Cancelled;
        CancelReason = reason.Trim();
        return Result.Success();
    }
}
