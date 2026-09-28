namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum LeaveSession
{
    FirstHalf = 0,
    SecondHalf,
}

/// <summary>
/// A leave policy (CL, SL, EL, ML, CompOff, LOP). Group-wide so that a transfer between branches keeps the same
/// entitlements.
/// </summary>
public sealed class LeaveType : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Days credited per calendar year (pro-rated for joiners).</summary>
    public decimal AnnualQuota { get; set; }

    /// <summary>Maximum unused days carried into the next year (0 = lapses).</summary>
    public decimal CarryForwardCap { get; set; }

    public bool IsEncashable { get; set; }

    /// <summary>Unpaid leave (LOP) reduces salary; paid leave does not.</summary>
    public bool IsPaid { get; set; } = true;

    public bool AllowHalfDay { get; set; } = true;

    /// <summary>False for LOP: no balance is kept, any number of days can be applied.</summary>
    public bool TracksBalance { get; set; } = true;

    /// <summary>Maternity leave runs in calendar days (holidays and week offs inside the period count).</summary>
    public bool CountsNonWorkingDays { get; set; }

    public decimal? MaxDaysPerRequest { get; set; }

    /// <summary>Requests longer than this need an attachment (for example a medical certificate for sick leave).</summary>
    public decimal? DocumentRequiredAfterDays { get; set; }

    /// <summary>Restricts the type to one gender (maternity leave); null = everyone.</summary>
    public Gender? ApplicableGender { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>An employee's entitlement ledger for one leave type and calendar year.</summary>
public sealed class LeaveBalance : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public int Year { get; set; }

    /// <summary>Carried forward from the previous year.</summary>
    public decimal Opening { get; set; }

    public decimal Credited { get; set; }

    /// <summary>Manual corrections and comp-off credits (+/−), each explained in the audit trail.</summary>
    public decimal Adjusted { get; set; }

    public decimal Availed { get; set; }
    public decimal Encashed { get; set; }

    public decimal Closing => Opening + Credited + Adjusted - Availed - Encashed;
}

/// <summary>
/// A leave application. Days are counted at application (excluding holidays and week offs unless the type counts
/// calendar days) and deducted from the balance only on approval; pending requests reserve their days so two
/// parallel applications cannot overdraw the balance.
/// </summary>
public sealed class LeaveRequest : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EmployeeId { get; set; }
    public Guid LeaveTypeId { get; set; }
    public DateOnly FromDate { get; set; }
    public LeaveSession FromSession { get; set; }
    public DateOnly ToDate { get; set; }
    public LeaveSession ToSession { get; set; } = LeaveSession.SecondHalf;
    public decimal Days { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? AttachmentFileId { get; set; }
    public ApprovalStatus Status { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    public bool Overlaps(DateOnly from, DateOnly to) => FromDate <= to && ToDate >= from;

    /// <summary>Fraction of <paramref name="date"/> covered by this request (0, 0.5 or 1), ignoring holidays.</summary>
    public decimal PortionOn(DateOnly date)
    {
        if (date < FromDate || date > ToDate)
        {
            return 0;
        }

        var portion = 1m;
        if (date == FromDate && FromSession == LeaveSession.SecondHalf)
        {
            portion -= 0.5m;
        }

        if (date == ToDate && ToSession == LeaveSession.FirstHalf)
        {
            portion -= 0.5m;
        }

        return Math.Max(portion, 0);
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
            return Error.Field("note", "Give a reason for rejecting the leave.");
        }

        Status = ApprovalStatus.Rejected;
        DecidedByUserId = approverUserId;
        DecidedAt = now;
        DecisionNote = note.Trim();
        return Result.Success();
    }

    /// <summary>
    /// Withdraws the request. Employees may withdraw pending leave, or approved leave that has not started;
    /// HR may cancel approved leave at any time (for example an employee who reported for duty after all).
    /// </summary>
    public Result Cancel(string reason, DateOnly today, bool byHr, DateTimeOffset now)
    {
        switch (Status)
        {
            case ApprovalStatus.Pending:
                break;
            case ApprovalStatus.Approved when byHr || FromDate > today:
                break;
            case ApprovalStatus.Approved:
                return Error.Rule("leave.started", "Leave that has already started can only be cancelled by HR.");
            default:
                return Error.Rule("leave.closed", "This request has already been rejected or cancelled.");
        }

        Status = ApprovalStatus.Cancelled;
        CancelledAt = now;
        CancelReason = reason.Trim();
        return Result.Success();
    }
}

public enum HolidayKind
{
    National = 0,
    Festival,

    /// <summary>Optional/restricted holiday: not a closure, so attendance treats it as a working day.</summary>
    Restricted,
}

/// <summary>A facility holiday (state and festival holidays differ between Tamil Nadu and Karnataka branches).</summary>
public sealed class Holiday : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = string.Empty;
    public HolidayKind Kind { get; set; }

    public bool IsClosure => Kind != HolidayKind.Restricted;
}
