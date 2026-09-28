using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Features.SelfService;

/// <summary>The signed-in employee's own record (identifiers masked exactly as HR sees them).</summary>
public sealed record MyProfileDto(
    Guid EmployeeId,
    string EmployeeCode,
    string FullName,
    string? DesignationName,
    string? DepartmentName,
    EmploymentType EmploymentType,
    EmployeeStatus Status,
    DateOnly JoiningDate,
    string? Phone,
    string? Email,
    string? ReportingManagerName,
    string? BiometricPin,
    SalaryPaymentMode PaymentMode,
    string? BankName,
    string? BankAccountMasked,
    string? BankIfscMasked,
    string? PanMasked,
    string? Uan,
    string? PfNumber,
    string? EsiNumber,
    int Reportees);

public sealed record MyPayslipItem(Guid Id, int Year, int Month, decimal GrossEarnings, decimal TotalDeductions, decimal NetPay, PayrollRunStatus Status);
