namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>
/// Employees' Provident Fund: 12 % of wages from the employee, matched by the employer, on wages capped at the ₹15,000
/// ceiling unless higher contributions are opted for. Wages are as the Code on Social Security 2020 defines them
/// (s.2(88)), in force since 21 November 2025, not basic pay alone. Contributions are rounded to the nearest rupee as
/// EPFO does.
/// </summary>
public static class ProvidentFund
{
    public const decimal WageCeiling = 15_000m;
    public const decimal EmployeeRate = 0.12m;
    public const decimal EmployerRate = 0.12m;

    /// <summary>
    /// The month's PF wages: the wages, and of the pay left out of them by clauses (a) to (i), what comes to more than half
    /// of the two together (s.2(88), first proviso).
    /// </summary>
    public static decimal Wages(decimal wages, decimal excluded) => wages + Math.Max(0, excluded - ((wages + excluded) / 2));

    /// <summary>The wages contributions are paid on: up to the ceiling, or in full on actual wages.</summary>
    public static decimal Wage(decimal wages, bool onActualWages) =>
        Math.Max(0, onActualWages ? wages : Math.Min(wages, WageCeiling));

    public static decimal EmployeeContribution(decimal pfWage) => RoundRupee(pfWage * EmployeeRate);

    public static decimal EmployerContribution(decimal pfWage) => RoundRupee(pfWage * EmployerRate);

    private static decimal RoundRupee(decimal amount) => Math.Round(amount, 0, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Employees' State Insurance (ESI Act 1948): employees whose contracted gross is at most ₹21,000 a month contribute
/// 0.75 % of the wages actually earned, the employer 3.25 %. Coverage is judged on contracted gross so that a
/// month with loss of pay does not pull someone into ESI. ESIC rounds contributions up to the next rupee.
/// </summary>
public static class EmployeeStateInsurance
{
    public const decimal WageCeiling = 21_000m;
    public const decimal EmployeeRate = 0.0075m;
    public const decimal EmployerRate = 0.0325m;

    public static bool IsCovered(decimal contractedMonthlyGross) => contractedMonthlyGross > 0 && contractedMonthlyGross <= WageCeiling;

    public static decimal EmployeeContribution(decimal esiWage) => esiWage <= 0 ? 0 : Math.Ceiling(esiWage * EmployeeRate);

    public static decimal EmployerContribution(decimal esiWage) => esiWage <= 0 ? 0 : Math.Ceiling(esiWage * EmployerRate);
}
