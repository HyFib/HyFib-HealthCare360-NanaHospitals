namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum RegularisationKind
{
    /// <summary>Forgot to punch in/out, or the terminal was down: supplies the missing times.</summary>
    MissedPunch = 0,

    /// <summary>Official duty away from the terminal (camp, training, patient transfer): the day counts as worked.</summary>
    OnDuty,
}

/// <summary>
/// An employee's request to correct a day's attendance, approved by their reporting manager or HR. Approved
/// requests are fed back into the attendance calculation instead of overwriting results, so later recomputation
/// (for example after a late device upload) never loses the correction.
/// </summary>
public sealed class AttendanceRegularisation : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }
    public RegularisationKind Kind { get; set; }

    /// <summary>Local (IST) in-time on <see cref="Date"/>.</summary>
    public TimeOnly? RequestedIn { get; set; }

    /// <summary>Local (IST) out-time; an out-time at or before the in-time is on the following day.</summary>
    public TimeOnly? RequestedOut { get; set; }

    public string Reason { get; set; } = string.Empty;
    public ApprovalStatus Status { get; set; }

    /// <summary>User who raised the request (the employee, or HR on their behalf).</summary>
    public Guid? RequestedByUserId { get; set; }

    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    /// <summary>The corrected punch times, in local time, as used by the attendance calculator.</summary>
    public IReadOnlyList<DateTime> RequestedPunches()
    {
        if (Status != ApprovalStatus.Approved || Kind != RegularisationKind.MissedPunch)
        {
            return [];
        }

        var punches = new List<DateTime>(2);
        if (RequestedIn is { } inTime)
        {
            punches.Add(Date.ToDateTime(inTime));
        }

        if (RequestedOut is { } outTime)
        {
            var outDate = RequestedIn is { } start && outTime <= start ? Date.AddDays(1) : Date;
            punches.Add(outDate.ToDateTime(outTime));
        }

        return punches;
    }

    public Result Approve(Guid approverUserId, DateTimeOffset now, string? note)
    {
        var check = ApprovalRules.CanDecide(Status, RequestedByUserId, approverUserId);
        if (check.IsFailure)
        {
            return check;
        }

        Status = ApprovalStatus.Approved;
        DecidedByUserId = approverUserId;
        DecidedAt = now;
        DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return Result.Success();
    }

    public Result Reject(Guid approverUserId, DateTimeOffset now, string? note)
    {
        var check = ApprovalRules.CanDecide(Status, RequestedByUserId, approverUserId);
        if (check.IsFailure)
        {
            return check;
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Give a reason for rejecting the request.");
        }

        Status = ApprovalStatus.Rejected;
        DecidedByUserId = approverUserId;
        DecidedAt = now;
        DecisionNote = note.Trim();
        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status != ApprovalStatus.Pending)
        {
            return Error.Rule("regularisation.not_pending", "Only pending requests can be withdrawn.");
        }

        Status = ApprovalStatus.Cancelled;
        return Result.Success();
    }
}
