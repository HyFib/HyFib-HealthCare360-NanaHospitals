namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum TransferRequestStatus
{
    /// <summary>Asked for by the sending ward; the bed is held; the receiving ward has not decided.</summary>
    Pending = 0,

    /// <summary>The receiving ward accepted it and the patient moved.</summary>
    Completed,

    /// <summary>An urgent step-up or step-down to or from critical care: the patient moved at once, and the receiving ward's acceptance is recorded afterwards.</summary>
    MovedAwaitingAcceptance,

    Declined,
    Cancelled,
}

/// <summary>
/// A move of a patient to a bed in another ward, as a hand-off between wards: the sending ward asks, with the reason and a
/// clinical summary for the receiving team; the bed asked for is held for the patient while the request waits; the
/// receiving ward's nurse in charge, or a doctor who may transfer patients, accepts it (never whoever asked), and the move
/// is then made as any transfer is (back-dating rules unchanged). A step-up or step-down to or from critical care may be
/// made at once as urgent: the patient moves first and the receiving ward records its acceptance within the hour.
/// </summary>
public sealed class TransferRequest : AuditableEntity, IFacilityScoped
{
    /// <summary>An urgent move to or from critical care is accepted within this time, or flagged.</summary>
    public static readonly TimeSpan UrgentAcceptanceWindow = TimeSpan.FromHours(1);

    /// <summary>How long the requested bed is held for the patient while the receiving ward decides.</summary>
    public static readonly TimeSpan BedHold = TimeSpan.FromHours(12);

    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? FromWardId { get; set; }
    public Guid? FromBedId { get; set; }
    public Guid ToWardId { get; set; }
    public Guid ToBedId { get; set; }
    public string Reason { get; set; } = string.Empty;

    /// <summary>The sending team's summary for the receiving team (diagnosis, condition, lines, what is pending).</summary>
    public string ClinicalSummary { get; set; } = string.Empty;

    public Guid? ConsultantPractitionerId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>A step-up or step-down to or from critical care, moved at once.</summary>
    public bool IsUrgent { get; set; }

    public TransferRequestStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? RequestedBy { get; set; }
    public string? RequestedByName { get; set; }

    /// <summary>When the held bed is released if nobody has decided.</summary>
    public DateTimeOffset? HoldUntil { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? DecidedByName { get; set; }
    public string? DecisionNote { get; set; }

    /// <summary>When the patient moved (for an urgent move, before the acceptance).</summary>
    public DateTimeOffset? TransferredAt { get; set; }

    /// <summary>The receiving ward was told, once, that an urgent move was not accepted in time.</summary>
    public DateTimeOffset? OverdueNotifiedAt { get; set; }

    public DateTimeOffset? AcceptanceDueAt => IsUrgent && TransferredAt is { } at ? at + UrgentAcceptanceWindow : null;

    /// <summary>An urgent move not accepted within the hour.</summary>
    public bool IsAcceptanceOverdue(DateTimeOffset now) =>
        Status == TransferRequestStatus.MovedAwaitingAcceptance && AcceptanceDueAt is { } due && due < now;

    /// <summary>Accepted after the hour (kept on the record when it was accepted late).</summary>
    public bool WasAcceptedLate => IsUrgent && DecidedAt is { } decided && AcceptanceDueAt is { } due && decided > due;

    public bool IsOpen => Status is TransferRequestStatus.Pending or TransferRequestStatus.MovedAwaitingAcceptance;

    public Result CheckDecider(Guid? userId)
    {
        if (!IsOpen)
        {
            return Error.Rule("transfer.decided", $"This transfer request is already {Describe(Status)}.");
        }

        return userId is not null && userId == RequestedBy
            ? Error.Rule("transfer.self_accept", "The receiving ward accepts a transfer: whoever asked for it cannot accept or decline it.")
            : Result.Success();
    }

    /// <summary>The receiving ward accepted: for a pending request the move follows; for an urgent move it is recorded.</summary>
    public void Accept(Guid? userId, string? userName, string? note, DateTimeOffset now, DateTimeOffset? transferredAt)
    {
        DecidedAt = now;
        DecidedBy = userId;
        DecidedByName = userName;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        TransferredAt ??= transferredAt;
        Status = TransferRequestStatus.Completed;
        HoldUntil = null;
    }

    public Result Decline(Guid? userId, string? userName, string reason, DateTimeOffset now)
    {
        if (Status != TransferRequestStatus.Pending)
        {
            return Error.Rule("transfer.not_pending",
                Status == TransferRequestStatus.MovedAwaitingAcceptance
                    ? "The patient has already moved on an urgent transfer: record the acceptance, and raise any concern with the sending team."
                    : $"This transfer request is already {Describe(Status)}.");
        }

        DecidedAt = now;
        DecidedBy = userId;
        DecidedByName = userName;
        DecisionNote = reason.Trim();
        Status = TransferRequestStatus.Declined;
        HoldUntil = null;
        return Result.Success();
    }

    public Result Cancel(Guid? userId, string? userName, string reason, DateTimeOffset now)
    {
        if (Status != TransferRequestStatus.Pending)
        {
            return Error.Rule("transfer.not_pending", $"This transfer request is already {Describe(Status)}.");
        }

        DecidedAt = now;
        DecidedBy = userId;
        DecidedByName = userName;
        DecisionNote = reason.Trim();
        Status = TransferRequestStatus.Cancelled;
        HoldUntil = null;
        return Result.Success();
    }

    public static string Describe(TransferRequestStatus status) => status switch
    {
        TransferRequestStatus.Pending => "waiting for the receiving ward",
        TransferRequestStatus.Completed => "accepted and the patient moved",
        TransferRequestStatus.MovedAwaitingAcceptance => "moved and waiting for the receiving ward's acceptance",
        TransferRequestStatus.Declined => "declined",
        _ => "cancelled",
    };
}
