namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>Preventive maintenance schedule for an asset (AST-03), e.g. quarterly ventilator PM.</summary>
public sealed class PreventiveMaintenancePlan : AuditableEntity
{
    public Guid AssetId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int FrequencyDays { get; set; }
    public DateOnly NextDueDate { get; set; }
    public DateOnly? LastDoneOn { get; private set; }
    public string? Checklist { get; set; }

    /// <summary>In-house engineer or service vendor responsible for the visit.</summary>
    public string? AssignedTo { get; set; }

    public Guid? VendorId { get; set; }
    public bool IsActive { get; set; } = true;

    public bool IsDue(DateOnly today, int withinDays) => IsActive && NextDueDate <= today.AddDays(withinDays);

    /// <summary>Records a completed visit; the next due date runs from the actual visit date.</summary>
    public Result RecordVisit(DateOnly doneOn)
    {
        if (FrequencyDays <= 0)
        {
            return Error.Rule("pm.invalid_frequency", "The plan needs a frequency in days.");
        }

        if (LastDoneOn is { } last && doneOn < last)
        {
            return Error.Rule("pm.visit_out_of_order", "This visit is older than the last recorded visit.");
        }

        LastDoneOn = doneOn;
        NextDueDate = doneOn.AddDays(FrequencyDays);
        return Result.Success();
    }
}

public enum PmVisitOutcome
{
    Completed = 0,
    CompletedWithIssues,
    NotDone,
}

public sealed class PmVisit : AuditableEntity
{
    public Guid PlanId { get; set; }
    public Guid AssetId { get; set; }
    public DateOnly VisitDate { get; set; }
    public string? PerformedBy { get; set; }
    public Guid? VendorId { get; set; }
    public PmVisitOutcome Outcome { get; set; }
    public string? Findings { get; set; }
    public decimal Cost { get; set; }
    public DateOnly NextDueDate { get; set; }
    public Guid? ServiceReportFileId { get; set; }
}

public enum TicketPriority
{
    Low = 0,
    Medium,
    High,
    Critical,
}

public enum TicketStatus
{
    Open = 0,
    Assigned,
    InProgress,
    Resolved,
    Closed,
}

/// <summary>
/// Breakdown ticket (AST-04). Downtime runs from the report until resolution (or is entered when the equipment
/// was usable meanwhile) and feeds equipment uptime reporting for NABH.
/// </summary>
public sealed class MaintenanceTicket : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid AssetId { get; set; }
    public Guid? ReportedByUserId { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
    public string Issue { get; set; } = string.Empty;
    public TicketPriority Priority { get; set; }
    public TicketStatus Status { get; private set; }
    public string? AssignedTo { get; private set; }
    public Guid? AssignedToUserId { get; private set; }
    public Guid? VendorId { get; private set; }
    public DateTimeOffset? AssignedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public string? Resolution { get; private set; }
    public string? RootCause { get; private set; }
    public decimal Cost { get; private set; }
    public int? DowntimeMinutes { get; private set; }

    public bool IsOpen => Status is TicketStatus.Open or TicketStatus.Assigned or TicketStatus.InProgress;

    public Result Assign(string assignee, Guid? userId, Guid? vendorId, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("ticket.closed", $"Ticket {Number} is {Status}.");
        }

        AssignedTo = assignee;
        AssignedToUserId = userId;
        VendorId = vendorId;
        AssignedAt = now;
        if (Status == TicketStatus.Open)
        {
            Status = TicketStatus.Assigned;
        }

        return Result.Success();
    }

    public Result Start(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("ticket.closed", $"Ticket {Number} is {Status}.");
        }

        Status = TicketStatus.InProgress;
        StartedAt ??= now;
        return Result.Success();
    }

    public Result Resolve(string resolution, string? rootCause, decimal cost, int? downtimeMinutes, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("ticket.closed", $"Ticket {Number} is {Status}.");
        }

        if (cost < 0 || downtimeMinutes < 0)
        {
            return Error.Rule("ticket.invalid_values", "Cost and downtime cannot be negative.");
        }

        Status = TicketStatus.Resolved;
        Resolution = resolution;
        RootCause = rootCause;
        Cost = cost;
        ResolvedAt = now;
        DowntimeMinutes = downtimeMinutes ?? (int)Math.Max(Math.Round((now - ReportedAt).TotalMinutes), 0);
        return Result.Success();
    }

    public Result Close(DateTimeOffset now)
    {
        if (Status != TicketStatus.Resolved)
        {
            return Error.Rule("ticket.not_resolved", "Resolve the ticket before closing it.");
        }

        Status = TicketStatus.Closed;
        ClosedAt = now;
        return Result.Success();
    }

    /// <summary>The fix did not hold: back to in-progress (downtime is recalculated on the next resolution).</summary>
    public Result Reopen()
    {
        if (Status != TicketStatus.Resolved)
        {
            return Error.Rule("ticket.not_resolved", "Only resolved tickets can be reopened.");
        }

        Status = TicketStatus.InProgress;
        ResolvedAt = null;
        DowntimeMinutes = null;
        return Result.Success();
    }
}

public enum CalibrationResult
{
    Pass = 0,
    Fail,
    Adjusted,
}

/// <summary>Calibration certificate for measuring equipment (AST-05, NABL/NABH requirement).</summary>
public sealed class CalibrationRecord : AuditableEntity
{
    public Guid AssetId { get; set; }
    public DateOnly CalibratedOn { get; set; }
    public string Agency { get; set; } = string.Empty;
    public string? CertificateNumber { get; set; }
    public Guid? CertificateFileId { get; set; }
    public DateOnly NextDueDate { get; set; }
    public CalibrationResult Outcome { get; set; }
    public string? Remarks { get; set; }
}

public enum CondemnationStatus
{
    Pending = 0,
    Approved,
    Rejected,
}

/// <summary>Request to condemn an asset (AST-06), decided by an asset manager other than the requester.</summary>
public sealed class CondemnationRequest : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AssetId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? RequestedByUserId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public decimal BookValueAtRequest { get; set; }
    public CondemnationStatus Status { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecisionRemarks { get; private set; }

    public Result Approve(Guid? userId, string? remarks, DateTimeOffset now) => Decide(CondemnationStatus.Approved, userId, remarks, now);

    public Result Reject(Guid? userId, string remarks, DateTimeOffset now) => Decide(CondemnationStatus.Rejected, userId, remarks, now);

    private Result Decide(CondemnationStatus status, Guid? userId, string? remarks, DateTimeOffset now)
    {
        if (Status != CondemnationStatus.Pending)
        {
            return Error.Rule("condemnation.decided", $"This request is already {Status}.");
        }

        if (userId is not null && userId == RequestedByUserId)
        {
            return Error.Forbidden("condemnation.self_approval", "A condemnation must be decided by someone other than the requester.");
        }

        Status = status;
        DecidedByUserId = userId;
        DecidedAt = now;
        DecisionRemarks = remarks;
        return Result.Success();
    }
}
