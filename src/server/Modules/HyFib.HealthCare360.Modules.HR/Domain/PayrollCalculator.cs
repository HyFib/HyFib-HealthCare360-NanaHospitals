namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>A pay head from the employee's salary structure.</summary>
public sealed record SalaryLineInput(
    string Code,
    string Name,
    SalaryComponentKind Kind,
    SalaryCalculationType CalculationType,
    decimal Value,
    int SortOrder);

/// <summary>A one-off earning or deduction for the month (arrears, canteen credit…).</summary>
public sealed record PayrollAdjustmentInput(string Code, string Name, SalaryComponentKind Kind, decimal Amount);

public sealed record StatutoryProfile(bool PfApplicable, bool PfOnActualBasic, bool EsiApplicable, ProfessionalTaxSchedule? ProfessionalTax);

public sealed record PayrollCalculationInput(
    int Year,
    int Month,
    int DaysInMonth,
    decimal PayableDays,
    IReadOnlyList<SalaryLineInput> Lines,
    IReadOnlyList<PayrollAdjustmentInput> Adjustments,
    StatutoryProfile Statutory,
    decimal MonthlyTds,
    decimal? TdsOverride = null,
    decimal? EarlierHalfYearGross = null);

public sealed record CalculatedPayLine(
    string Code,
    string Name,
    SalaryComponentKind Kind,
    decimal FullAmount,
    decimal Amount,
    bool IsStatutory,
    int SortOrder);

public sealed record PayrollCalculationResult(
    IReadOnlyList<CalculatedPayLine> Lines,
    decimal StandardGross,
    decimal GrossEarnings,
    decimal TotalDeductions,
    decimal NetPay,
    decimal PfWage,
    decimal EmployeePf,
    decimal EmployerPf,
    decimal EsiWage,
    decimal EmployeeEsi,
    decimal EmployerEsi,
    decimal ProfessionalTax,
    decimal Tds,
    IReadOnlyList<string> Warnings,
    bool HasErrors);

/// <summary>
/// Computes one payslip (HR-06/07). Earnings from the structure are pro-rated by payable days ÷ days in month;
/// percentage heads are a percentage of basic. One-off inputs are added as they are. Statutory deductions follow:
/// PF on earned basic (₹15,000 ceiling), ESI on earned gross when contracted gross ≤ ₹21,000, state professional
/// tax, and the projected monthly TDS (or this month's override). Structure deductions are not pro-rated.
/// A negative net pay is reported as an error rather than silently capped, because it means the inputs are wrong.
/// </summary>
public static class PayrollCalculator
{
    public const string BasicCode = SalaryComponent.BasicCode;

    public static PayrollCalculationResult Calculate(PayrollCalculationInput input)
    {
        var warnings = new List<string>();
        var hasErrors = false;
        var lines = new List<CalculatedPayLine>();

        var basicLine = input.Lines.FirstOrDefault(l => l.Code == BasicCode && l.Kind == SalaryComponentKind.Earning);
        if (basicLine is null || basicLine.CalculationType != SalaryCalculationType.Fixed)
        {
            warnings.Add("The salary structure has no fixed BASIC component.");
            hasErrors = true;
        }

        var fullBasic = basicLine?.CalculationType == SalaryCalculationType.Fixed ? basicLine.Value : 0;
        var factor = input.DaysInMonth <= 0 ? 0 : Math.Clamp(input.PayableDays, 0, input.DaysInMonth) / input.DaysInMonth;
        var earnedBasic = Money.Round(fullBasic * factor);
        if (input.PayableDays <= 0)
        {
            warnings.Add("No payable days this month.");
        }

        var standardGross = 0m;
        foreach (var line in input.Lines.Where(l => l.Kind == SalaryComponentKind.Earning).OrderBy(l => l.SortOrder))
        {
            var full = FullAmount(line, fullBasic);
            standardGross += full;
            lines.Add(new CalculatedPayLine(line.Code, line.Name, SalaryComponentKind.Earning, full, Money.Round(full * factor), false, line.SortOrder));
        }

        foreach (var adjustment in input.Adjustments.Where(a => a.Kind == SalaryComponentKind.Earning && a.Amount != 0))
        {
            lines.Add(new CalculatedPayLine(adjustment.Code, adjustment.Name, SalaryComponentKind.Earning, 0, Money.Round(adjustment.Amount), false, 500));
        }

        var gross = lines.Where(l => l.Kind == SalaryComponentKind.Earning).Sum(l => l.Amount);

        var pfWage = input.Statutory.PfApplicable ? ProvidentFund.Wage(earnedBasic, input.Statutory.PfOnActualBasic) : 0;
        var employeePf = ProvidentFund.EmployeeContribution(pfWage);
        var employerPf = ProvidentFund.EmployerContribution(pfWage);

        var esiCovered = input.Statutory.EsiApplicable && EmployeeStateInsurance.IsCovered(standardGross);
        var esiWage = esiCovered ? gross : 0;
        var employeeEsi = EmployeeStateInsurance.EmployeeContribution(esiWage);
        var employerEsi = EmployeeStateInsurance.EmployerContribution(esiWage);

        var halfYearIncome = input.EarlierHalfYearGross is { } earlier ? earlier + gross : (decimal?)null;
        var professionalTax = ProfessionalTaxCalculator.Compute(input.Statutory.ProfessionalTax, input.Month, gross, halfYearIncome);
        var tds = Money.Round(Math.Max(0, input.TdsOverride ?? input.MonthlyTds));

        AddStatutory(lines, SalaryComponent.PfCode, "Provident Fund (employee)", employeePf, 900);
        AddStatutory(lines, SalaryComponent.EsiCode, "ESI (employee)", employeeEsi, 910);
        AddStatutory(lines, SalaryComponent.ProfessionalTaxCode, "Professional Tax", professionalTax, 920);
        AddStatutory(lines, SalaryComponent.TdsCode, "Income Tax (TDS)", tds, 930);

        foreach (var line in input.Lines.Where(l => l.Kind == SalaryComponentKind.Deduction).OrderBy(l => l.SortOrder))
        {
            var amount = line.CalculationType == SalaryCalculationType.PercentOfBasic ? Money.PercentOf(earnedBasic, line.Value) : Money.Round(line.Value);
            lines.Add(new CalculatedPayLine(line.Code, line.Name, SalaryComponentKind.Deduction, FullAmount(line, fullBasic), amount, false, 940 + line.SortOrder));
        }

        foreach (var adjustment in input.Adjustments.Where(a => a.Kind == SalaryComponentKind.Deduction && a.Amount != 0))
        {
            lines.Add(new CalculatedPayLine(adjustment.Code, adjustment.Name, SalaryComponentKind.Deduction, 0, Money.Round(adjustment.Amount), false, 990));
        }

        var deductions = lines.Where(l => l.Kind == SalaryComponentKind.Deduction).Sum(l => l.Amount);
        var net = gross - deductions;
        if (net < 0)
        {
            warnings.Add($"Deductions exceed earnings by ₹{Money.FormatInr(-net)}. Reduce the recoveries or TDS for this month.");
            hasErrors = true;
        }

        return new PayrollCalculationResult(
            lines, standardGross, gross, deductions, net, pfWage, employeePf, employerPf, esiWage, employeeEsi, employerEsi,
            professionalTax, tds, warnings, hasErrors);
    }

    private static decimal FullAmount(SalaryLineInput line, decimal fullBasic) =>
        line.CalculationType == SalaryCalculationType.PercentOfBasic ? Money.PercentOf(fullBasic, line.Value) : Money.Round(line.Value);

    private static void AddStatutory(List<CalculatedPayLine> lines, string code, string name, decimal amount, int sortOrder)
    {
        if (amount > 0)
        {
            lines.Add(new CalculatedPayLine(code, name, SalaryComponentKind.Deduction, amount, amount, true, sortOrder));
        }
    }
}
