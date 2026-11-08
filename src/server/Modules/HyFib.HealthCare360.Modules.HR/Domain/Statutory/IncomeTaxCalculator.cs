namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

/// <summary>What the employee declared for the year (Form 12BB), used by the old regime; the new regime ignores all but other income and the previous employer.</summary>
public sealed record TaxDeclarationAmounts(
    decimal RentPaid = 0,
    bool MetroCity = false,
    decimal Section80C = 0,
    decimal Section80Ccd1B = 0,
    decimal Section80DSelf = 0,
    decimal Section80DParents = 0,
    bool ParentsSenior = false,
    decimal Section80E = 0,
    decimal Section80G = 0,
    decimal Section80Tta = 0,
    decimal HousingLoanInterest = 0,
    decimal OtherIncome = 0,
    decimal PreviousEmployerSalary = 0,
    decimal PreviousEmployerTds = 0)
{
    public static TaxDeclarationAmounts None { get; } = new();
}

/// <summary>A year's salary from this employer: paid so far plus projected to March.</summary>
public sealed record AnnualSalary(decimal Gross, decimal Basic, decimal HouseRentAllowance, decimal EmployeePf, decimal ProfessionalTax);

/// <summary>Every step of the computation, as Form 16 Part B sets it out.</summary>
public sealed record IncomeTaxComputation(
    string FinancialYear,
    TaxRegime Regime,
    decimal GrossSalary,
    decimal HouseRentExemption,
    decimal StandardDeduction,
    decimal ProfessionalTax,
    decimal IncomeFromSalary,
    decimal HousePropertyLoss,
    decimal OtherIncome,
    decimal GrossTotalIncome,
    decimal Deduction80C,
    decimal Deduction80Ccd1B,
    decimal Deduction80D,
    decimal Deduction80E,
    decimal Deduction80G,
    decimal Deduction80Tta,
    decimal TotalDeductions,
    decimal TaxableIncome,
    decimal TaxOnIncome,
    decimal Rebate87A,
    decimal Surcharge,
    decimal Cess,
    decimal TotalTax)
{
    public decimal ChapterVIA => TotalDeductions;
}

/// <summary>
/// Income tax on salary for one financial year (s.192: the employer deducts the tax on the estimated income of the year).
/// Taxable income is rounded to the nearest ₹10 (s.288A) and the tax to the nearest ₹10 (s.288B). The s.87A rebate applies
/// to residents up to the year's income limit, and in the new regime with marginal relief (tax no more than the income
/// above the limit). Surcharge has marginal relief at each band; health and education cess is on tax plus surcharge.
/// </summary>
public static class IncomeTaxCalculator
{
    public static IncomeTaxComputation Compute(IncomeTaxYear year, TaxRegime regime, AnnualSalary salary, TaxDeclarationAmounts declared, DateOnly? dateOfBirth)
    {
        ArgumentNullException.ThrowIfNull(year);
        ArgumentNullException.ThrowIfNull(salary);
        ArgumentNullException.ThrowIfNull(declared);
        var rates = year.For(regime);
        var old = regime == TaxRegime.Old;
        var gross = Math.Max(0, salary.Gross + declared.PreviousEmployerSalary);

        var hra = old ? HouseRentExemption(salary.HouseRentAllowance, salary.Basic, declared.RentPaid, declared.MetroCity) : 0;
        var standard = Math.Min(rates.StandardDeduction, Math.Max(0, gross - hra));
        var professionalTax = old ? Math.Min(2_500, Math.Max(0, salary.ProfessionalTax)) : 0;
        var fromSalary = Math.Max(0, gross - hra - standard - professionalTax);

        // A self-occupied home's interest is a loss from house property, set off up to the cap (s.24(b), s.71(3A)); the new
        // regime allows neither.
        var houseLoss = old ? Math.Min(Math.Max(0, declared.HousingLoanInterest), year.OldHousingInterestCap) : 0;
        var other = Math.Max(0, declared.OtherIncome);
        var grossTotal = Math.Max(0, fromSalary - houseLoss + other);

        decimal d80C = 0, d80Ccd = 0, d80D = 0, d80E = 0, d80G = 0, d80Tta = 0;
        if (old)
        {
            var senior = IsSenior(dateOfBirth, year.StartYear);
            d80C = Math.Min(year.Old80CCap, Math.Max(0, declared.Section80C) + Math.Max(0, salary.EmployeePf));
            d80Ccd = Math.Min(year.Old80Ccd1BCap, Math.Max(0, declared.Section80Ccd1B));
            d80D = Math.Min(senior ? year.Old80DSelfSeniorCap : year.Old80DSelfCap, Math.Max(0, declared.Section80DSelf))
                   + Math.Min(declared.ParentsSenior ? year.Old80DParentsSeniorCap : year.Old80DParentsCap, Math.Max(0, declared.Section80DParents));
            d80E = Math.Max(0, declared.Section80E);
            d80G = Math.Max(0, declared.Section80G);
            d80Tta = Math.Min(year.Old80TtaCap, Math.Max(0, declared.Section80Tta));
        }

        var deductions = Math.Min(grossTotal, d80C + d80Ccd + d80D + d80E + d80G + d80Tta);
        var taxable = RoundToTen(grossTotal - deductions);
        var slabs = SlabsFor(rates, regime, dateOfBirth, year.StartYear);
        var tax = SlabTax(slabs, taxable);

        var rebate = 0m;
        if (taxable <= rates.RebateIncomeLimit)
        {
            rebate = Math.Min(tax, rates.RebateMaximum);
        }
        else if (rates.RebateMarginalRelief && tax > taxable - rates.RebateIncomeLimit)
        {
            // Marginal relief: the tax on income just above the limit is no more than that income above it.
            rebate = tax - (taxable - rates.RebateIncomeLimit);
        }

        var afterRebate = tax - rebate;
        var surcharge = Surcharge(rates, slabs, taxable, afterRebate);
        var cess = Money.Round((afterRebate + surcharge) * year.CessRate);
        var total = RoundToTen(afterRebate + surcharge + cess);

        return new IncomeTaxComputation(
            year.Label, regime, Money.Round(gross), Money.Round(hra), Money.Round(standard), Money.Round(professionalTax), Money.Round(fromSalary),
            Money.Round(houseLoss), Money.Round(other), Money.Round(grossTotal), d80C, d80Ccd, d80D, d80E, d80G, d80Tta, Money.Round(deductions),
            taxable, Money.Round(tax), Money.Round(rebate), Money.Round(surcharge), cess, total);
    }

    /// <summary>
    /// s.10(13A), rule 2A: the least of the allowance received, rent paid less 10 % of salary, and half of salary in Mumbai,
    /// Delhi, Kolkata or Chennai (two-fifths elsewhere). Salary here is basic pay (and dearness allowance where it counts).
    /// </summary>
    public static decimal HouseRentExemption(decimal allowance, decimal salary, decimal rentPaid, bool metro)
    {
        if (allowance <= 0 || rentPaid <= 0)
        {
            return 0;
        }

        var rentOverTenth = Math.Max(0, rentPaid - (salary * 0.10m));
        var share = salary * (metro ? 0.50m : 0.40m);
        return Money.Round(Math.Min(allowance, Math.Min(rentOverTenth, share)));
    }

    /// <summary>Slab tax on <paramref name="income"/>.</summary>
    public static decimal SlabTax(IReadOnlyList<TaxSlab> slabs, decimal income)
    {
        ArgumentNullException.ThrowIfNull(slabs);
        var tax = 0m;
        for (var i = 0; i < slabs.Count; i++)
        {
            var from = slabs[i].From;
            var to = i + 1 < slabs.Count ? slabs[i + 1].From : decimal.MaxValue;
            if (income > from)
            {
                tax += (Math.Min(income, to) - from) * slabs[i].Rate;
            }
        }

        return tax;
    }

    /// <summary>s.288A/288B: to the nearest multiple of ten rupees (five and above goes up).</summary>
    public static decimal RoundToTen(decimal amount) => Math.Max(0, Math.Round(amount / 10m, 0, MidpointRounding.AwayFromZero) * 10m);

    /// <summary>Sixty or more at any time in the year: born on or before 1 April sixty years before the year's end (a person attains an age the day before the birthday).</summary>
    public static bool IsSenior(DateOnly? dateOfBirth, int fyStartYear, int age = 60) =>
        dateOfBirth is { } dob && dob <= new DateOnly(fyStartYear + 1, 4, 1).AddYears(-age);

    private static IReadOnlyList<TaxSlab> SlabsFor(RegimeRates rates, TaxRegime regime, DateOnly? dob, int fyStartYear)
    {
        if (regime != TaxRegime.Old)
        {
            return rates.Slabs;
        }

        if (rates.SuperSeniorSlabs is { } super && IsSenior(dob, fyStartYear, 80))
        {
            return super;
        }

        return rates.SeniorSlabs is { } senior && IsSenior(dob, fyStartYear) ? senior : rates.Slabs;
    }

    /// <summary>
    /// Surcharge at the band the income falls in, with marginal relief: tax plus surcharge is no more than the tax (and
    /// surcharge) at the band's threshold plus the income above it.
    /// </summary>
    private static decimal Surcharge(RegimeRates rates, IReadOnlyList<TaxSlab> slabs, decimal income, decimal tax)
    {
        var band = rates.Surcharge.LastOrDefault(b => income > b.Above);
        if (band is null)
        {
            return 0;
        }

        var surcharge = tax * band.Rate;
        var below = rates.Surcharge.LastOrDefault(b => b.Above < band.Above);
        var atThreshold = SlabTax(slabs, band.Above) * (1 + (below?.Rate ?? 0));
        var ceiling = atThreshold + (income - band.Above);
        return Math.Max(0, tax + surcharge > ceiling ? ceiling - tax : surcharge);
    }
}

/// <summary>Salary figures for the months already paid this year, this month, and each month projected to March.</summary>
public sealed record MonthSalary(decimal Gross, decimal Basic, decimal HouseRentAllowance, decimal EmployeePf, decimal ProfessionalTax)
{
    public static MonthSalary Zero { get; } = new(0, 0, 0, 0, 0);

    public static MonthSalary operator +(MonthSalary a, MonthSalary b) =>
        new(a.Gross + b.Gross, a.Basic + b.Basic, a.HouseRentAllowance + b.HouseRentAllowance, a.EmployeePf + b.EmployeePf, a.ProfessionalTax + b.ProfessionalTax);

    public MonthSalary Times(int months) => new(Gross * months, Basic * months, HouseRentAllowance * months, EmployeePf * months, ProfessionalTax * months);
}

public sealed record TdsProjection(IncomeTaxComputation Computation, decimal AnnualTax, decimal DeductedSoFar, decimal MonthsLeft, decimal ThisMonth, bool WithoutPan);

/// <summary>
/// The month's TDS on salary (s.192(1)): the year's tax on salary paid so far, this month's and the present pay projected to
/// March, less the tax already deducted (here and by a previous employer), spread evenly over this and the remaining
/// months. Without a PAN, s.206AA asks for the higher of that and 20 % of the income (no cess on the 20 %); with no tax
/// on the income at all, nothing is deducted.
/// </summary>
public static class TdsProjector
{
    public const decimal NoPanRate = 0.20m;

    public static TdsProjection Project(
        IncomeTaxYear year, TaxRegime regime, MonthSalary paidSoFar, decimal tdsSoFar, MonthSalary thisMonth, MonthSalary eachLaterMonth,
        int laterMonths, TaxDeclarationAmounts declared, DateOnly? dateOfBirth, bool hasPan)
    {
        ArgumentNullException.ThrowIfNull(paidSoFar);
        ArgumentNullException.ThrowIfNull(thisMonth);
        ArgumentNullException.ThrowIfNull(eachLaterMonth);
        ArgumentNullException.ThrowIfNull(declared);
        var annual = paidSoFar + thisMonth + eachLaterMonth.Times(Math.Max(0, laterMonths));
        var computation = IncomeTaxCalculator.Compute(year, regime, new AnnualSalary(annual.Gross, annual.Basic, annual.HouseRentAllowance, annual.EmployeePf, annual.ProfessionalTax),
            declared, dateOfBirth);

        var tax = computation.TotalTax;
        var withoutPan = !hasPan && tax > 0;
        if (withoutPan)
        {
            tax = Math.Max(tax, Math.Round(computation.TaxableIncome * NoPanRate, 0, MidpointRounding.AwayFromZero));
        }

        var deducted = Math.Max(0, tdsSoFar) + Math.Max(0, declared.PreviousEmployerTds);
        var months = Math.Max(0, laterMonths) + 1;
        var thisMonthTds = Math.Max(0, Math.Round((tax - deducted) / months, 0, MidpointRounding.AwayFromZero));
        return new TdsProjection(computation, tax, deducted, months, thisMonthTds, withoutPan);
    }
}
