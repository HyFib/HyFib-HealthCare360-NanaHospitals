using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Features.Payroll;

public sealed record SalaryComponentDto(
    Guid Id,
    string Code,
    string Name,
    SalaryComponentKind Kind,
    SalaryCalculationType CalculationType,
    decimal DefaultValue,
    bool IsTaxable,
    bool IsStatutory,
    int SortOrder,
    bool IsActive,
    uint Version);

public sealed record SaveSalaryComponentRequest(
    string Code,
    string Name,
    SalaryComponentKind Kind,
    SalaryCalculationType CalculationType,
    decimal DefaultValue,
    bool IsTaxable,
    int SortOrder,
    bool IsActive,
    uint? Version);

public sealed record ProfessionalTaxBandDto(decimal IncomeFrom, decimal? IncomeTo, decimal Amount, decimal? FebruaryAmount);

/// <param name="Version">The version of the slabs, which are saved together (REL-04).</param>
public sealed record ProfessionalTaxScheduleDto(
    string StateCode,
    string StateName,
    ProfessionalTaxFrequency Frequency,
    IReadOnlyList<int> DeductionMonths,
    IReadOnlyList<ProfessionalTaxBandDto> Bands,
    uint Version);

/// <summary>
/// Replaces a state's whole slab table (rates change by notification, never one band at a time). <see cref="Version"/> is
/// the version of the table as loaded (REL-04); none for a state that has none yet.
/// </summary>
public sealed record SaveProfessionalTaxRequest(
    string StateName,
    ProfessionalTaxFrequency Frequency,
    IReadOnlyList<int> DeductionMonths,
    IReadOnlyList<ProfessionalTaxBandDto> Bands,
    uint? Version = null);

public sealed record SalaryStructureLineInput(Guid SalaryComponentId, decimal Value);

public sealed record SaveSalaryStructureRequest(
    DateOnly EffectiveFrom,
    decimal? AnnualCtc,
    decimal MonthlyTds,
    bool PfApplicable,
    bool PfOnActualBasic,
    bool EsiApplicable,
    string? ProfessionalTaxState,
    string? Notes,
    IReadOnlyList<SalaryStructureLineInput> Lines);

public sealed record SalaryStructureLineDto(
    Guid SalaryComponentId,
    string Code,
    string Name,
    SalaryComponentKind Kind,
    SalaryCalculationType CalculationType,
    decimal Value,
    decimal MonthlyAmount);

public sealed record SalaryStructureDto(
    Guid Id,
    Guid EmployeeId,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    decimal? AnnualCtc,
    decimal MonthlyTds,
    bool PfApplicable,
    bool PfOnActualBasic,
    bool EsiApplicable,
    string? ProfessionalTaxState,
    string? Notes,
    decimal MonthlyGross,
    IReadOnlyList<SalaryStructureLineDto> Lines);

public sealed record PayrollRunDto(
    Guid Id,
    int Year,
    int Month,
    PayrollRunStatus Status,
    int EmployeeCount,
    decimal TotalGross,
    decimal TotalDeductions,
    decimal TotalNet,
    DateTimeOffset? ProcessedAt,
    DateTimeOffset? ApprovedAt,
    DateOnly? PaidOn,
    string? PaymentReference,
    DateTimeOffset? LockedAt,
    string? Notes,
    int PayslipsWithErrors,
    uint Version);

public sealed record CreatePayrollRunRequest(int Year, int Month, string? Notes);

public sealed record PayrollProcessResult(int Payslips, int Skipped, int PayslipsWithErrors, IReadOnlyList<string> Messages);

public sealed record MarkPayrollPaidRequest(DateOnly PaidOn, string Reference);

public sealed record ReopenPayrollRequest(string Reason);

public sealed record PayrollInputDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    PayrollInputKind Kind,
    string ComponentCode,
    string Description,
    decimal Amount,
    PayrollInputSource Source,
    string? SourceReference);

/// <summary>
/// A variable item for the month. For <see cref="PayrollInputKind.LopDaysOverride"/> the amount is a number of days;
/// for <see cref="PayrollInputKind.TdsOverride"/> it replaces the projected TDS.
/// </summary>
public sealed record SavePayrollInputRequest(Guid EmployeeId, PayrollInputKind Kind, string? ComponentCode, string Description, decimal Amount);

/// <summary>CSV text: <c>employeeCode,componentCode,amount[,description]</c> (header optional).</summary>
public sealed record ImportPayrollInputsRequest(string Csv);

public sealed record PayrollImportResult(int Imported);

public sealed record PayslipListItem(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string? DepartmentName,
    decimal PayableDays,
    decimal LopDays,
    decimal GrossEarnings,
    decimal TotalDeductions,
    decimal NetPay,
    bool HasErrors);

public sealed record PayslipLineDto(string Code, string Name, decimal FullAmount, decimal Amount, bool IsStatutory);

/// <summary>Everything needed to render or print a payslip.</summary>
public sealed record PayslipDto(
    Guid Id,
    Guid PayrollRunId,
    PayrollRunStatus RunStatus,
    int Year,
    int Month,
    string? FacilityName,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string? DesignationName,
    string? DepartmentName,
    string? PanMasked,
    string? Uan,
    int DaysInMonth,
    decimal PayableDays,
    decimal LopDays,
    int MissingAttendanceDays,
    IReadOnlyList<PayslipLineDto> Earnings,
    IReadOnlyList<PayslipLineDto> Deductions,
    decimal GrossEarnings,
    decimal TotalDeductions,
    decimal NetPay,
    decimal PfWage,
    decimal EmployerPf,
    decimal EsiWage,
    decimal EmployerEsi,
    SalaryPaymentMode PaymentMode,
    string? BankAccountMasked,
    IReadOnlyList<string> Warnings,
    bool HasErrors);

public sealed class SaveSalaryComponentRequestValidator : AbstractValidator<SaveSalaryComponentRequest>
{
    public SaveSalaryComponentRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Must(c => c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
            .WithMessage("Use letters, digits or '_'.")
            .Must(c => !SalaryComponent.StatutoryCodes.Contains(c.Trim().ToUpperInvariant()))
            .WithMessage("PF, ESI, PT and TDS are computed by payroll and cannot be edited as components.");
        RuleFor(x => x.Name).NotEmpty().SafeText(100);
        RuleFor(x => x.DefaultValue).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DefaultValue).LessThanOrEqualTo(100).When(x => x.CalculationType == SalaryCalculationType.PercentOfBasic)
            .WithMessage("A percentage of basic cannot exceed 100.");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 800);
    }
}

public sealed class SaveProfessionalTaxRequestValidator : AbstractValidator<SaveProfessionalTaxRequest>
{
    public SaveProfessionalTaxRequestValidator()
    {
        RuleFor(x => x.StateName).NotEmpty().SafeText(64);
        RuleFor(x => x.Bands).NotEmpty().Must(b => b.Count <= 20).WithMessage("At most 20 bands.");
        RuleFor(x => x)
            .Custom((request, context) =>
            {
                var errors = ProfessionalTaxCalculator.Validate(
                    request.Bands.Select(b => new ProfessionalTaxBand(b.IncomeFrom, b.Amount, b.FebruaryAmount)).ToList(),
                    request.Frequency,
                    request.DeductionMonths);
                foreach (var error in errors)
                {
                    context.AddFailure("bands", error);
                }
            });
    }
}

public sealed class SaveSalaryStructureRequestValidator : AbstractValidator<SaveSalaryStructureRequest>
{
    public SaveSalaryStructureRequestValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().Must(l => l.Count <= 30).WithMessage("At most 30 pay heads.")
            .Must(l => l.Select(x => x.SalaryComponentId).Distinct().Count() == l.Count).WithMessage("Each pay head can appear once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.SalaryComponentId).NotEmpty();
            line.RuleFor(l => l.Value).InclusiveBetween(0, 10_000_000);
        });
        RuleFor(x => x.MonthlyTds).InclusiveBetween(0, 10_000_000);
        RuleFor(x => x.AnnualCtc).InclusiveBetween(0, 1_000_000_000);
        RuleFor(x => x.ProfessionalTaxState).Matches("^[A-Z]{2,4}$").When(x => !string.IsNullOrEmpty(x.ProfessionalTaxState))
            .WithMessage("Use the state code, for example TN or KA.");
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class CreatePayrollRunRequestValidator : AbstractValidator<CreatePayrollRunRequest>
{
    public CreatePayrollRunRequestValidator()
    {
        RuleFor(x => x.Year).InclusiveBetween(2000, 2100);
        RuleFor(x => x.Month).InclusiveBetween(1, 12);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class MarkPayrollPaidRequestValidator : AbstractValidator<MarkPayrollPaidRequest>
{
    public MarkPayrollPaidRequestValidator() => RuleFor(x => x.Reference).NotEmpty().SafeText(100);
}

public sealed class ReopenPayrollRequestValidator : AbstractValidator<ReopenPayrollRequest>
{
    public ReopenPayrollRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(500);
}

public sealed class SavePayrollInputRequestValidator : AbstractValidator<SavePayrollInputRequest>
{
    public SavePayrollInputRequestValidator()
    {
        RuleFor(x => x.EmployeeId).NotEmpty();
        RuleFor(x => x.Description).NotEmpty().SafeText(200);
        RuleFor(x => x.ComponentCode).NotEmpty().MaximumLength(24)
            .When(x => x.Kind is PayrollInputKind.Earning or PayrollInputKind.Deduction)
            .WithMessage("Choose the pay head.");
        RuleFor(x => x.Amount).GreaterThan(0).LessThanOrEqualTo(PayrollInputCsvParser.MaxAmount)
            .When(x => x.Kind is PayrollInputKind.Earning or PayrollInputKind.Deduction);
        RuleFor(x => x.Amount).InclusiveBetween(0, 31).When(x => x.Kind == PayrollInputKind.LopDaysOverride)
            .WithMessage("Loss-of-pay days must be between 0 and 31.");
        RuleFor(x => x.Amount).InclusiveBetween(0, PayrollInputCsvParser.MaxAmount).When(x => x.Kind == PayrollInputKind.TdsOverride);
    }
}

public sealed class ImportPayrollInputsRequestValidator : AbstractValidator<ImportPayrollInputsRequest>
{
    public ImportPayrollInputsRequestValidator() =>
        RuleFor(x => x.Csv).NotEmpty().MaximumLength(1024 * 1024).WithMessage("The file must be at most 1 MB.");
}
