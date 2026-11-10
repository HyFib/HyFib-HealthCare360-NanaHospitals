using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Features.Leave;

public sealed record LeaveTypeDto(
    Guid Id,
    string Code,
    string Name,
    decimal AnnualQuota,
    decimal CarryForwardCap,
    bool IsEncashable,
    bool IsPaid,
    bool AllowHalfDay,
    bool TracksBalance,
    bool CountsNonWorkingDays,
    decimal? MaxDaysPerRequest,
    decimal? DocumentRequiredAfterDays,
    Gender? ApplicableGender,
    bool IsActive,
    uint Version,
    decimal? InServiceEncashmentDaysPerYear = null,
    decimal MinimumBalanceAfterEncashment = 0);

public sealed record SaveLeaveTypeRequest(
    string Code,
    string Name,
    decimal AnnualQuota,
    decimal CarryForwardCap,
    bool IsEncashable,
    bool IsPaid,
    bool AllowHalfDay,
    bool TracksBalance,
    bool CountsNonWorkingDays,
    decimal? MaxDaysPerRequest,
    decimal? DocumentRequiredAfterDays,
    Gender? ApplicableGender,
    bool IsActive,
    uint? Version,
    decimal? InServiceEncashmentDaysPerYear = null,
    decimal MinimumBalanceAfterEncashment = 0);

public sealed record HolidayDto(Guid Id, DateOnly Date, string Name, HolidayKind Kind);

public sealed record SaveHolidayRequest(DateOnly Date, string Name, HolidayKind Kind);

public sealed record LeaveBalanceDto(
    Guid? Id,
    Guid LeaveTypeId,
    string LeaveTypeCode,
    string LeaveTypeName,
    int Year,
    decimal Opening,
    decimal Credited,
    decimal Adjusted,
    decimal Availed,
    decimal Encashed,
    decimal Closing,
    decimal Pending,
    decimal Available);

public sealed record CreditLeaveRequest(int Year);

public sealed record CreditLeaveResult(int Employees, int BalancesCreated);

/// <summary>Manual correction or comp-off credit (+) / debit (−); the reason is written to the audit trail.</summary>
public sealed record AdjustLeaveBalanceRequest(Guid EmployeeId, Guid LeaveTypeId, int Year, decimal Days, string Reason);

public sealed record ApplyLeaveRequest(
    Guid LeaveTypeId,
    DateOnly FromDate,
    LeaveSession FromSession,
    DateOnly ToDate,
    LeaveSession ToSession,
    string Reason,
    Guid? AttachmentFileId);

public sealed record LeaveRequestDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    Guid LeaveTypeId,
    string LeaveTypeCode,
    DateOnly FromDate,
    LeaveSession FromSession,
    DateOnly ToDate,
    LeaveSession ToSession,
    decimal Days,
    string Reason,
    ApprovalStatus Status,
    DateTimeOffset AppliedAt,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    Guid? AttachmentFileId);

public sealed record CancelLeaveRequest(string Reason);

public sealed class SaveLeaveTypeRequestValidator : AbstractValidator<SaveLeaveTypeRequest>
{
    public SaveLeaveTypeRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Must(c => c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
            .WithMessage("Use letters, digits or '_'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(100);
        RuleFor(x => x.AnnualQuota).InclusiveBetween(0, 366);
        RuleFor(x => x.CarryForwardCap).InclusiveBetween(0, 366);
        RuleFor(x => x.MaxDaysPerRequest).InclusiveBetween(0.5m, 366);
        RuleFor(x => x.DocumentRequiredAfterDays).InclusiveBetween(0, 366);
        RuleFor(x => x.AnnualQuota).Must(q => q * 2 == Math.Floor(q * 2)).WithMessage("Use whole or half days.");
        RuleFor(x => x.InServiceEncashmentDaysPerYear).InclusiveBetween(0.5m, 366)
            .Must(d => d is null || d * 2 == Math.Floor(d.Value * 2)).WithMessage("Use whole or half days.");
        RuleFor(x => x.InServiceEncashmentDaysPerYear).Null().When(x => !x.IsEncashable || !x.TracksBalance)
            .WithMessage("Only an encashable leave type that keeps a balance can be paid out in service.");
        RuleFor(x => x.MinimumBalanceAfterEncashment).InclusiveBetween(0, 366)
            .Must(d => d * 2 == Math.Floor(d * 2)).WithMessage("Use whole or half days.");
    }
}

public sealed class SaveHolidayRequestValidator : AbstractValidator<SaveHolidayRequest>
{
    public SaveHolidayRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().SafeText(100);
        RuleFor(x => x.Date).Must(d => d.Year is >= 2000 and <= 2100).WithMessage("Enter a valid date.");
    }
}

public sealed class CreditLeaveRequestValidator : AbstractValidator<CreditLeaveRequest>
{
    public CreditLeaveRequestValidator() => RuleFor(x => x.Year).InclusiveBetween(2000, 2100);
}

public sealed class AdjustLeaveBalanceRequestValidator : AbstractValidator<AdjustLeaveBalanceRequest>
{
    public AdjustLeaveBalanceRequestValidator()
    {
        RuleFor(x => x.EmployeeId).NotEmpty();
        RuleFor(x => x.LeaveTypeId).NotEmpty();
        RuleFor(x => x.Year).InclusiveBetween(2000, 2100);
        RuleFor(x => x.Days).NotEqual(0).InclusiveBetween(-366, 366)
            .Must(d => d * 2 == Math.Floor(d * 2)).WithMessage("Use whole or half days.");
        RuleFor(x => x.Reason).NotEmpty().SafeText(300);
    }
}

public sealed class ApplyLeaveRequestValidator : AbstractValidator<ApplyLeaveRequest>
{
    public ApplyLeaveRequestValidator()
    {
        RuleFor(x => x.LeaveTypeId).NotEmpty();
        RuleFor(x => x.ToDate).GreaterThanOrEqualTo(x => x.FromDate).WithMessage("The leave ends before it starts.");
        RuleFor(x => x.ToDate)
            .Must((x, to) => to.DayNumber - x.FromDate.DayNumber < LeaveDayCounter.MaxSpanDays)
            .WithMessage("Apply for at most one year at a time.");
        RuleFor(x => x)
            .Must(x => !(x.FromDate == x.ToDate && x.FromSession == LeaveSession.SecondHalf && x.ToSession == LeaveSession.FirstHalf))
            .WithName("toSession").WithMessage("A one-day leave cannot run from the second half to the first half.");
        RuleFor(x => x.Reason).NotEmpty().SafeText(500);
    }
}

public sealed class CancelLeaveRequestValidator : AbstractValidator<CancelLeaveRequest>
{
    public CancelLeaveRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

/// <summary>
/// An in-service leave encashment asked for: days of one leave type and year, paid in the payroll of
/// <paramref name="PayYear"/>/<paramref name="PayMonth"/> once someone else who approves payroll approves it.
/// </summary>
public sealed record EncashLeaveRequest(Guid EmployeeId, Guid LeaveTypeId, int Year, decimal Days, int PayYear, int PayMonth, string Reason);

/// <summary>An encashment made: in service through payroll, or on leaving in the full and final settlement.</summary>
public sealed record LeaveEncashmentDto(
    Guid Id,
    Guid LeaveTypeId,
    string LeaveTypeCode,
    int Year,
    LeaveEncashmentKind Kind,
    decimal Days,
    decimal DailyRate,
    decimal Amount,
    Guid? PayrollRunId,
    string? PayrollMonth,
    DateOnly? LastWorkingDay,
    string Reason,
    DateTimeOffset CreatedAt);

/// <summary>An encashment waiting for a second person, for the employee's leave screen.</summary>
public sealed record PendingLeaveEncashment(Guid ApprovalId, Guid LeaveTypeId, int Year, decimal Days, decimal Amount, string Summary, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>What an employee may encash now, per leave type that pays out in service.</summary>
public sealed record EncashableLeaveDto(Guid LeaveTypeId, string LeaveTypeCode, string LeaveTypeName, int Year, decimal CapPerYear, decimal MinimumBalance,
    decimal EncashedThisYear, decimal Waiting, decimal MostDays, decimal DailyRate);

public sealed class EncashLeaveRequestValidator : AbstractValidator<EncashLeaveRequest>
{
    public EncashLeaveRequestValidator()
    {
        RuleFor(x => x.EmployeeId).NotEmpty();
        RuleFor(x => x.LeaveTypeId).NotEmpty();
        RuleFor(x => x.Year).InclusiveBetween(2000, 2100);
        RuleFor(x => x.Days).GreaterThan(0).LessThanOrEqualTo(366)
            .Must(d => d * 2 == Math.Floor(d * 2)).WithMessage("Use whole or half days.");
        RuleFor(x => x.PayYear).InclusiveBetween(2000, 2100);
        RuleFor(x => x.PayMonth).InclusiveBetween(1, 12);
        RuleFor(x => x.Reason).NotEmpty().SafeText(300);
    }
}

