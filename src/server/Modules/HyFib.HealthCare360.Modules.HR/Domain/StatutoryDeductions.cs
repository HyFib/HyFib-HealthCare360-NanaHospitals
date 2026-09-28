namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>
/// Employees' Provident Fund (EPF Act 1952): 12 % of basic wages from the employee, matched by the employer, on
/// wages capped at the ₹15,000 statutory ceiling unless higher contributions are opted for. Contributions are
/// rounded to the nearest rupee as EPFO does.
/// </summary>
public static class ProvidentFund
{
    public const decimal WageCeiling = 15_000m;
    public const decimal EmployeeRate = 0.12m;
    public const decimal EmployerRate = 0.12m;

    public static decimal Wage(decimal earnedBasic, bool onActualBasic) =>
        Math.Max(0, onActualBasic ? earnedBasic : Math.Min(earnedBasic, WageCeiling));

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
