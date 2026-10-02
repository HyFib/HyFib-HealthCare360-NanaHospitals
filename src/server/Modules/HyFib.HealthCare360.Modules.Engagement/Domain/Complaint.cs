namespace HyFib.HealthCare360.Modules.Engagement.Domain;

public enum ComplaintSource
{
    Feedback = 0,
    WalkIn,
    Phone,
    Email,
    Portal,
}

public enum ComplaintStatus
{
    Open = 0,
    InProgress,
    Resolved,
    Closed,
}

public enum ComplaintPriority
{
    Normal = 0,
    High,
}

/// <summary>
/// A patient complaint (FB-03), raised automatically from low feedback scores or recorded by staff. Each complaint has
/// a resolution deadline by priority (24 h high, 72 h normal) and its turnaround time is reported.
/// </summary>
public sealed class Complaint : AuditableEntity, IFacilityScoped
{
    public static readonly TimeSpan HighPrioritySla = TimeSpan.FromHours(24);
    public static readonly TimeSpan NormalPrioritySla = TimeSpan.FromHours(72);

    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}CMP{YY}{SEQ:5}".</summary>
    public string ComplaintNumber { get; set; } = string.Empty;

    public ComplaintSource Source { get; set; }
    public Guid? FeedbackResponseId { get; set; }
    public Guid? PatientId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>Area complained about (usually a feedback aspect: Doctor, Nursing, Billing…).</summary>
    public string? Category { get; set; }

    public string Description { get; set; } = string.Empty;
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public ComplaintPriority Priority { get; set; }
    public Guid? OwnerUserId { get; set; }
    public ComplaintStatus Status { get; set; }
    public string? Resolution { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public int ReopenCount { get; set; }

    /// <summary>When the complaint passed its deadline unresolved and was escalated (FB-03); it is escalated once.</summary>
    public DateTimeOffset? EscalatedAt { get; private set; }

    /// <summary>Time from being raised to resolution (the turnaround the hospital reports).</summary>
    public TimeSpan? TurnaroundTime => ResolvedAt - RaisedAt;

    public bool IsOverdue(DateTimeOffset now) => Status is ComplaintStatus.Open or ComplaintStatus.InProgress && now > DueAt;

    /// <summary>
    /// Escalates a complaint that is still unresolved after its deadline. Returns false when it is not overdue or was
    /// already escalated, so the escalation alert goes out once (a complaint reopened after escalation is not escalated again).
    /// </summary>
    public bool Escalate(DateTimeOffset now)
    {
        if (!IsOverdue(now) || EscalatedAt is not null)
        {
            return false;
        }

        EscalatedAt = now;
        return true;
    }

    public static TimeSpan SlaFor(ComplaintPriority priority) => priority == ComplaintPriority.High ? HighPrioritySla : NormalPrioritySla;

    /// <summary>Starts the resolution clock when the complaint is registered.</summary>
    public void Register(DateTimeOffset now)
    {
        RaisedAt = now;
        DueAt = now + SlaFor(Priority);
    }

    /// <summary>Raising the priority tightens the deadline; lowering it never extends a deadline already promised.</summary>
    public void ChangePriority(ComplaintPriority priority)
    {
        Priority = priority;
        var due = RaisedAt + SlaFor(priority);
        if (due < DueAt)
        {
            DueAt = due;
        }
    }

    public Result MoveTo(ComplaintStatus target, string? note, DateTimeOffset now)
    {
        switch (Status, target)
        {
            case (ComplaintStatus.Open, ComplaintStatus.InProgress):
                break;
            case (ComplaintStatus.Open or ComplaintStatus.InProgress, ComplaintStatus.Resolved):
                if (string.IsNullOrWhiteSpace(note))
                {
                    return Error.Field("note", "Describe how the complaint was resolved.");
                }

                Resolution = note.Trim();
                ResolvedAt = now;
                break;
            case (ComplaintStatus.Resolved, ComplaintStatus.Closed):
                ClosedAt = now;
                break;
            case (ComplaintStatus.Resolved, ComplaintStatus.InProgress):
                if (string.IsNullOrWhiteSpace(note))
                {
                    return Error.Field("note", "Say why the complaint is being reopened.");
                }

                ReopenCount++;
                ResolvedAt = null;
                break;
            default:
                return Error.Rule("complaint.invalid_transition", $"A complaint cannot move from {Status} to {target}.");
        }

        Status = target;
        return Result.Success();
    }
}
