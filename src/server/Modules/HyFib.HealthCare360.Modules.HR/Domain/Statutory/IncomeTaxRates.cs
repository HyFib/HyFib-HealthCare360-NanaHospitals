namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

/// <summary>The two ways an individual's income tax is worked out (s.115BAC; the new regime is the default from FY 2023-24).</summary>
public enum TaxRegime
{
    /// <summary>s.115BAC(1A): lower slabs, few deductions. The default when the employee says nothing.</summary>
    New = 0,

    /// <summary>The older slabs with Chapter VI-A deductions (80C, 80D…), house rent exemption and professional tax.</summary>
    Old,
}

/// <summary>Taxed at <see cref="Rate"/> on the part of income above <see cref="From"/> (and up to the next slab's start).</summary>
public sealed record TaxSlab(decimal From, decimal Rate);

/// <summary>A surcharge rate on tax for income above <see cref="Above"/>.</summary>
public sealed record SurchargeBand(decimal Above, decimal Rate);

/// <summary>The slabs, rebate and deductions of one regime for one financial year.</summary>
public sealed record RegimeRates(
    IReadOnlyList<TaxSlab> Slabs,
    decimal StandardDeduction,
    decimal RebateIncomeLimit,
    decimal RebateMaximum,
    bool RebateMarginalRelief,
    IReadOnlyList<SurchargeBand> Surcharge,
    IReadOnlyList<TaxSlab>? SeniorSlabs = null,
    IReadOnlyList<TaxSlab>? SuperSeniorSlabs = null);

/// <summary>
/// One financial year's income-tax rates for salaried individuals, with where they come from. HC360 looks the year up; the
/// computation (<see cref="IncomeTaxCalculator"/>) has no year in it.
/// </summary>
public sealed record IncomeTaxYear(
    int StartYear,
    RegimeRates New,
    RegimeRates Old,
    decimal CessRate,
    decimal Old80CCap,
    decimal Old80Ccd1BCap,
    decimal Old80DSelfCap,
    decimal Old80DSelfSeniorCap,
    decimal Old80DParentsCap,
    decimal Old80DParentsSeniorCap,
    decimal Old80TtaCap,
    decimal OldHousingInterestCap,
    string Source)
{
    /// <summary>"2025-26".</summary>
    public string Label => $"{StartYear}-{(StartYear + 1) % 100:00}";

    /// <summary>The assessment year for the return: "2026-27" for FY 2025-26.</summary>
    public string AssessmentYear => $"{StartYear + 1}-{(StartYear + 2) % 100:00}";

    public RegimeRates For(TaxRegime regime) => regime == TaxRegime.Old ? Old : New;
}

/// <summary>
/// Income-tax rates by financial year: a dated table, one row a year, each with its source. A year that is not here cannot
/// be computed, and payroll falls back to the TDS typed in the salary structure with a warning, so a new year's rates are
/// added here (with the Finance Act that sets them) before April's payroll.
/// </summary>
public static class IncomeTaxRates
{
    private static readonly IReadOnlyList<TaxSlab> OldSlabs = [new(0, 0), new(250_000, 0.05m), new(500_000, 0.20m), new(1_000_000, 0.30m)];
    private static readonly IReadOnlyList<TaxSlab> OldSeniorSlabs = [new(0, 0), new(300_000, 0.05m), new(500_000, 0.20m), new(1_000_000, 0.30m)];
    private static readonly IReadOnlyList<TaxSlab> OldSuperSeniorSlabs = [new(0, 0), new(500_000, 0.20m), new(1_000_000, 0.30m)];

    private static readonly IReadOnlyList<SurchargeBand> OldSurcharge =
        [new(5_000_000, 0.10m), new(10_000_000, 0.15m), new(20_000_000, 0.25m), new(50_000_000, 0.37m)];

    /// <summary>The new regime's surcharge stops at 25 % (s.115BAC(1A), Finance Act 2023).</summary>
    private static readonly IReadOnlyList<SurchargeBand> NewSurcharge = [new(5_000_000, 0.10m), new(10_000_000, 0.15m), new(20_000_000, 0.25m)];

    private static readonly RegimeRates OldRegime = new(OldSlabs, 50_000, 500_000, 12_500, false, OldSurcharge, OldSeniorSlabs, OldSuperSeniorSlabs);

    private static readonly RegimeRates NewFrom2025 = new(
        [new(0, 0), new(400_000, 0.05m), new(800_000, 0.10m), new(1_200_000, 0.15m), new(1_600_000, 0.20m), new(2_000_000, 0.25m), new(2_400_000, 0.30m)],
        75_000, 1_200_000, 60_000, true, NewSurcharge);

    public static IReadOnlyList<IncomeTaxYear> Years { get; } =
    [
        new(2024,
            new RegimeRates(
                [new(0, 0), new(300_000, 0.05m), new(700_000, 0.10m), new(1_000_000, 0.15m), new(1_200_000, 0.20m), new(1_500_000, 0.30m)],
                75_000, 700_000, 25_000, true, NewSurcharge),
            OldRegime, 0.04m, 150_000, 50_000, 25_000, 50_000, 25_000, 50_000, 10_000, 200_000,
            "Finance (No. 2) Act 2024: s.115BAC(1A) slabs, standard deduction ₹75,000 (s.16(ia)); s.87A rebate ₹25,000 up to ₹7 lakh."),
        new(2025, NewFrom2025, OldRegime, 0.04m, 150_000, 50_000, 25_000, 50_000, 25_000, 50_000, 10_000, 200_000,
            "Finance Act 2025: s.115BAC(1A) slabs from ₹4 lakh, s.87A rebate ₹60,000 up to ₹12 lakh with marginal relief."),

        // The Income-tax Act 2025 replaces the 1961 Act from 1 April 2026 (tax year 2026-27) and renumbers the sections
        // (s.192 salary TDS becomes s.392, s.194J becomes s.393). The rates carried into it are those of FY 2025-26.
        new(2026, NewFrom2025, OldRegime, 0.04m, 150_000, 50_000, 25_000, 50_000, 25_000, 50_000, 10_000, 200_000,
            "Income-tax Act 2025, First Schedule as enacted: the FY 2025-26 rates carried forward. Check against the Finance Act 2026."),
    ];

    /// <summary>The financial year (its start year) a date falls in: April to March.</summary>
    public static int FinancialYearOf(DateOnly date) => date.Month >= 4 ? date.Year : date.Year - 1;

    public static int FinancialYearOf(int year, int month) => month >= 4 ? year : year - 1;

    public static IncomeTaxYear? Find(int startYear) => Years.FirstOrDefault(y => y.StartYear == startYear);
}
