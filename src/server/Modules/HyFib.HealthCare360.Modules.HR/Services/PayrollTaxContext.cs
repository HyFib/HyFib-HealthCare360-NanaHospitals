using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>Salary already paid in the financial year, from earlier payslips.</summary>
public sealed record YearToDate(decimal Gross, decimal Basic, decimal HouseRent, decimal EmployeePf, decimal ProfessionalTax, decimal Tds)
{
    public static YearToDate None { get; } = new(0, 0, 0, 0, 0, 0);

    public MonthSalary ToSalary() => new(Gross, Basic, HouseRent, EmployeePf, ProfessionalTax);
}

/// <summary>
/// What payroll needs, beyond the month itself, to compute each employee's TDS on salary (s.192): this financial year's
/// earlier payslips, the declaration (regime and Form 12BB), and who was covered by ESI earlier in the contribution period.
/// </summary>
public sealed class PayrollTaxContext
{
    public const string HouseRentCode = "HRA";

    private PayrollTaxContext(int financialYear, IReadOnlyDictionary<Guid, YearToDate> yearToDate, IReadOnlyDictionary<Guid, TaxDeclaration> declarations,
        IReadOnlySet<Guid> esiCovered)
    {
        FinancialYear = financialYear;
        YearToDateOf = yearToDate;
        Declarations = declarations;
        EsiCoveredInPeriod = esiCovered;
    }

    public int FinancialYear { get; }

    public IReadOnlyDictionary<Guid, YearToDate> YearToDateOf { get; }

    public IReadOnlyDictionary<Guid, TaxDeclaration> Declarations { get; }

    public IReadOnlySet<Guid> EsiCoveredInPeriod { get; }

    public static async Task<PayrollTaxContext> LoadAsync(HospitalDbContext db, PayrollRun run, IReadOnlyCollection<Guid> employeeIds, CancellationToken ct)
    {
        var fy = IncomeTaxRates.FinancialYearOf(run.Year, run.Month);
        var currentKey = (run.Year * 100) + run.Month;
        var yearToDate = await YearToDateAsync(db, employeeIds, (fy * 100) + 4, currentKey, ct);

        var declarations = await db.Set<TaxDeclaration>().AsNoTracking()
            .Where(d => employeeIds.Contains(d.EmployeeId) && d.FinancialYear == fy)
            .ToDictionaryAsync(d => d.EmployeeId, ct);

        var (startYear, startMonth) = EsiContributionPeriod.Start(run.Year, run.Month);
        var periodKey = (startYear * 100) + startMonth;
        var covered = await db.Set<Payslip>().AsNoTracking()
            .Where(p => employeeIds.Contains(p.EmployeeId) && p.EmployeeEsi > 0 && (p.Year * 100) + p.Month >= periodKey && (p.Year * 100) + p.Month < currentKey)
            .Select(p => p.EmployeeId)
            .Distinct()
            .ToListAsync(ct);

        return new PayrollTaxContext(fy, yearToDate, declarations, covered.ToHashSet());
    }

    /// <summary>Totals of the payslips from <paramref name="fromKey"/> (yyyymm) up to, not including, <paramref name="beforeKey"/>.</summary>
    public static async Task<IReadOnlyDictionary<Guid, YearToDate>> YearToDateAsync(HospitalDbContext db, IReadOnlyCollection<Guid> employeeIds, int fromKey, int beforeKey,
        CancellationToken ct)
    {
        var totals = await db.Set<Payslip>().AsNoTracking()
            .Where(p => employeeIds.Contains(p.EmployeeId) && (p.Year * 100) + p.Month >= fromKey && (p.Year * 100) + p.Month < beforeKey)
            .GroupBy(p => p.EmployeeId)
            .Select(g => new { g.Key, Gross = g.Sum(p => p.GrossEarnings), Pf = g.Sum(p => p.EmployeePf), Pt = g.Sum(p => p.ProfessionalTax), Tds = g.Sum(p => p.Tds) })
            .ToListAsync(ct);
        var lines = await (
                from l in db.Set<PayslipLine>().AsNoTracking()
                join p in db.Set<Payslip>() on l.PayslipId equals p.Id
                where employeeIds.Contains(p.EmployeeId) && (p.Year * 100) + p.Month >= fromKey && (p.Year * 100) + p.Month < beforeKey
                      && l.Kind == SalaryComponentKind.Earning && (l.Code == SalaryComponent.BasicCode || l.Code == HouseRentCode)
                group l by new { p.EmployeeId, l.Code } into g
                select new { g.Key.EmployeeId, g.Key.Code, Amount = g.Sum(l => l.Amount) })
            .ToListAsync(ct);

        return totals.ToDictionary(t => t.Key, t => new YearToDate(
            t.Gross,
            lines.Where(l => l.EmployeeId == t.Key && l.Code == SalaryComponent.BasicCode).Sum(l => l.Amount),
            lines.Where(l => l.EmployeeId == t.Key && l.Code == HouseRentCode).Sum(l => l.Amount),
            t.Pf, t.Pt, t.Tds));
    }

    /// <summary>
    /// The month's TDS: the year's tax on salary paid so far, this month's and the present structure's full month for each
    /// month left to March (or to the last working day), less what was deducted, spread over this and the months left. A year
    /// without rates falls back to the TDS typed in the structure, with a warning.
    /// </summary>
    public decimal MonthlyTds(PayrollRun run, Employee employee, EmployeeSalaryStructure structure, ProfessionalTaxSchedule? schedule, PayrollCalculationResult month,
        List<string> warnings)
    {
        if (IncomeTaxRates.Find(FinancialYear) is not { } rates)
        {
            warnings.Add($"No income-tax rates are set up for {FinancialYear}-{(FinancialYear + 1) % 100:00}: TDS is the amount in the salary structure.");
            return structure.MonthlyTds;
        }

        var ytd = YearToDateOf.GetValueOrDefault(employee.Id) ?? YearToDate.None;
        var declaration = Declarations.GetValueOrDefault(employee.Id);
        var basic = month.Lines.FirstOrDefault(l => l.Code == SalaryComponent.BasicCode && l.Kind == SalaryComponentKind.Earning);
        var houseRent = month.Lines.FirstOrDefault(l => l.Code == HouseRentCode && l.Kind == SalaryComponentKind.Earning);
        var thisMonth = new MonthSalary(month.GrossEarnings, basic?.Amount ?? 0, houseRent?.Amount ?? 0, month.EmployeePf, month.ProfessionalTax);
        var later = new MonthSalary(month.StandardGross, basic?.FullAmount ?? 0, houseRent?.FullAmount ?? 0, month.EmployeePf,
            schedule?.Frequency == ProfessionalTaxFrequency.Monthly ? month.ProfessionalTax : 0);

        var projection = TdsProjector.Project(rates, declaration?.Regime ?? TaxRegime.New, ytd.ToSalary(), ytd.Tds, thisMonth, later,
            MonthsLeftAfter(run.Year, run.Month, employee.LeavingDate), declaration?.ToAmounts() ?? TaxDeclarationAmounts.None, employee.DateOfBirth,
            employee.PanCipher is not null);
        if (projection.WithoutPan)
        {
            warnings.Add("No PAN on record: TDS is at least 20% of the income (s.206AA).");
        }

        return projection.ThisMonth;
    }

    /// <summary>Months after this one to March, stopping at the month of the last working day.</summary>
    public static int MonthsLeftAfter(int year, int month, DateOnly? leavingDate)
    {
        var fy = IncomeTaxRates.FinancialYearOf(year, month);
        var current = new DateOnly(year, month, 1);
        var last = new DateOnly(fy + 1, 3, 1);
        if (leavingDate is { } leaving && new DateOnly(leaving.Year, leaving.Month, 1) < last)
        {
            last = new DateOnly(leaving.Year, leaving.Month, 1);
        }

        return Math.Max(0, ((last.Year - current.Year) * 12) + last.Month - current.Month);
    }
}
