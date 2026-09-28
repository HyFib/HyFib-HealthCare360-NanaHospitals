namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum PayrollRunStatus
{
    /// <summary>Created; inputs (arrears, canteen recoveries…) are being collected.</summary>
    Draft = 0,

    /// <summary>Payslips computed; can be re-processed after corrections.</summary>
    Processed,

    /// <summary>Checked and approved by someone other than the processor; payslips visible to employees.</summary>
    Approved,

    /// <summary>Bank transfer done.</summary>
    Paid,

    /// <summary>Closed for good; the month's attendance is locked too.</summary>
    Locked,
}

/// <summary>
/// A month's payroll for one facility. The status machine enforces maker-checker (the approver must differ from the
/// processor) and prevents edits once money has moved.
/// </summary>
public sealed class PayrollRun : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public PayrollRunStatus Status { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public Guid? ProcessedByUserId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateOnly? PaidOn { get; set; }
    public string? PaymentReference { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public int EmployeeCount { get; set; }
    public decimal TotalGross { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal TotalNet { get; set; }
    public string? Notes { get; set; }
    public string? ReopenReason { get; set; }

    public DateOnly PeriodStart => new(Year, Month, 1);

    public DateOnly PeriodEnd => new(Year, Month, DateTime.DaysInMonth(Year, Month));

    /// <summary>Inputs and payslips may change only before approval.</summary>
    public bool IsEditable => Status is PayrollRunStatus.Draft or PayrollRunStatus.Processed;

    public Result MarkProcessed(Guid? userId, DateTimeOffset now, int employeeCount, decimal gross, decimal deductions, decimal net)
    {
        if (!IsEditable)
        {
            return Error.Rule("payroll.not_editable", "An approved or paid payroll cannot be processed again. Reopen it first.");
        }

        Status = PayrollRunStatus.Processed;
        ProcessedByUserId = userId;
        ProcessedAt = now;
        EmployeeCount = employeeCount;
        TotalGross = gross;
        TotalDeductions = deductions;
        TotalNet = net;
        return Result.Success();
    }

    public Result Approve(Guid approverUserId, DateTimeOffset now)
    {
        if (Status != PayrollRunStatus.Processed)
        {
            return Error.Rule("payroll.not_processed", "Only a processed payroll can be approved.");
        }

        if (ProcessedByUserId == approverUserId)
        {
            return Error.Forbidden("payroll.maker_checker", "The person who processed the payroll cannot approve it.");
        }

        Status = PayrollRunStatus.Approved;
        ApprovedByUserId = approverUserId;
        ApprovedAt = now;
        return Result.Success();
    }

    /// <summary>Sends an approved payroll back for corrections (before any money moves).</summary>
    public Result Reopen(string reason)
    {
        if (Status != PayrollRunStatus.Approved)
        {
            return Error.Rule("payroll.not_reopenable", "Only an approved payroll that has not been paid can be reopened.");
        }

        Status = PayrollRunStatus.Processed;
        ApprovedByUserId = null;
        ApprovedAt = null;
        ReopenReason = reason.Trim();
        return Result.Success();
    }

    public Result MarkPaid(DateOnly paidOn, string reference)
    {
        if (Status != PayrollRunStatus.Approved)
        {
            return Error.Rule("payroll.not_approved", "Only an approved payroll can be marked as paid.");
        }

        if (paidOn < PeriodStart)
        {
            return Error.Field("paidOn", "The payment date cannot be before the payroll month.");
        }

        Status = PayrollRunStatus.Paid;
        PaidOn = paidOn;
        PaymentReference = reference.Trim();
        return Result.Success();
    }

    public Result Lock(DateTimeOffset now)
    {
        if (Status != PayrollRunStatus.Paid)
        {
            return Error.Rule("payroll.not_paid", "Only a paid payroll can be locked.");
        }

        Status = PayrollRunStatus.Locked;
        LockedAt = now;
        return Result.Success();
    }
}

public enum PayrollInputKind
{
    /// <summary>One-off earning (arrears, incentive, night allowance).</summary>
    Earning = 0,

    /// <summary>One-off recovery (canteen credit, salary advance, uniform).</summary>
    Deduction,

    /// <summary>Replaces attendance-based loss-of-pay days (Amount = days).</summary>
    LopDaysOverride,

    /// <summary>Replaces the projected monthly TDS for this month.</summary>
    TdsOverride,
}

public enum PayrollInputSource
{
    Manual = 0,
    Import,

    /// <summary>Posted by another module (for example canteen staff credit).</summary>
    Integration,
}

/// <summary>A variable pay item for one employee in one payroll month.</summary>
public sealed class PayrollInput : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public PayrollInputKind Kind { get; set; }

    /// <summary>Pay head shown on the payslip (for example CANTEEN); ignored for overrides.</summary>
    public string ComponentCode { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public PayrollInputSource Source { get; set; }

    /// <summary>Idempotency key for integrations (for example the canteen bill id).</summary>
    public string? SourceReference { get; set; }
}
