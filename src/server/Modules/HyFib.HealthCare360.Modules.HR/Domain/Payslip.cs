namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>
/// One employee's pay for a payroll month. Names, designation and the masked account are snapshots, so a payslip
/// reprinted years later shows what was true when it was paid.
/// </summary>
public sealed class Payslip : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PayrollRunId { get; set; }
    public Guid EmployeeId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public string? DesignationName { get; set; }
    public string? DepartmentName { get; set; }
    public int DaysInMonth { get; set; }
    public decimal PayableDays { get; set; }
    public decimal LopDays { get; set; }
    public int MissingAttendanceDays { get; set; }
    public decimal StandardGross { get; set; }
    public decimal GrossEarnings { get; set; }
    public decimal TotalDeductions { get; set; }
    public decimal NetPay { get; set; }
    public decimal PfWage { get; set; }
    public decimal EmployeePf { get; set; }
    public decimal EmployerPf { get; set; }
    public decimal EsiWage { get; set; }
    public decimal EmployeeEsi { get; set; }
    public decimal EmployerEsi { get; set; }
    public decimal ProfessionalTax { get; set; }
    public decimal Tds { get; set; }
    public SalaryPaymentMode PaymentMode { get; set; }
    public string? BankAccountMasked { get; set; }
    public List<string> Warnings { get; set; } = [];

    /// <summary>Blocks approval (for example deductions exceed earnings).</summary>
    public bool HasErrors { get; set; }

    public List<PayslipLine> Lines { get; set; } = [];
}

/// <summary>An earning or deduction on a payslip.</summary>
public sealed class PayslipLine : Entity
{
    public Guid PayslipId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SalaryComponentKind Kind { get; set; }

    /// <summary>Full-month contracted amount (before loss of pay).</summary>
    public decimal FullAmount { get; set; }

    public decimal Amount { get; set; }
    public bool IsStatutory { get; set; }
    public int SortOrder { get; set; }
}
