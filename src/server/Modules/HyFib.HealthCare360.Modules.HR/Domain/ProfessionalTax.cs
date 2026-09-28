namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum ProfessionalTaxFrequency
{
    /// <summary>Deducted every month on that month's salary (Karnataka).</summary>
    Monthly = 0,

    /// <summary>Levied on half-yearly income and deducted in the collection months (Tamil Nadu local bodies).</summary>
    HalfYearly,
}

/// <summary>
/// One income band of a state's professional tax. Bands are matched by their lower bound only (the band with the
/// highest <see cref="IncomeFrom"/> not above the income applies), so there are never gaps between bands.
/// Rates change by state notification, so they are data (editable by HR), not code.
/// </summary>
public sealed class ProfessionalTaxSlab : AuditableEntity
{
    /// <summary>Two-letter state code (TN, KA…).</summary>
    public string StateCode { get; set; } = string.Empty;

    public string StateName { get; set; } = string.Empty;
    public ProfessionalTaxFrequency Frequency { get; set; }

    /// <summary>Months (1–12) in which a half-yearly tax is deducted; ignored for monthly states.</summary>
    public List<int> DeductionMonths { get; set; } = [];

    public decimal IncomeFrom { get; set; }

    /// <summary>Informational upper bound shown to users.</summary>
    public decimal? IncomeTo { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Different amount in February (to reach the ₹2,500 annual constitutional cap); null = same as other months.</summary>
    public decimal? FebruaryAmount { get; set; }
}

public sealed record ProfessionalTaxBand(decimal IncomeFrom, decimal Amount, decimal? FebruaryAmount = null);

public sealed record ProfessionalTaxSchedule(
    string StateCode,
    ProfessionalTaxFrequency Frequency,
    IReadOnlyList<int> DeductionMonths,
    IReadOnlyList<ProfessionalTaxBand> Bands)
{
    public static ProfessionalTaxSchedule? From(IReadOnlyList<ProfessionalTaxSlab> slabs)
    {
        if (slabs.Count == 0)
        {
            return null;
        }

        var first = slabs[0];
        return new ProfessionalTaxSchedule(
            first.StateCode,
            first.Frequency,
            first.DeductionMonths,
            slabs.OrderBy(s => s.IncomeFrom).Select(s => new ProfessionalTaxBand(s.IncomeFrom, s.Amount, s.FebruaryAmount)).ToList());
    }
}

/// <summary>Pure professional-tax rules.</summary>
public static class ProfessionalTaxCalculator
{
    /// <summary>
    /// Tax for <paramref name="month"/>. Monthly states use that month's gross (with the February amount where
    /// defined). Half-yearly states charge only in their deduction months, on the half-year's income
    /// (<paramref name="halfYearIncome"/>; when unknown, the month's gross × 6 is used as a projection).
    /// </summary>
    public static decimal Compute(ProfessionalTaxSchedule? schedule, int month, decimal monthlyGross, decimal? halfYearIncome = null)
    {
        if (schedule is null || schedule.Bands.Count == 0 || monthlyGross <= 0)
        {
            return 0;
        }

        if (schedule.Frequency == ProfessionalTaxFrequency.HalfYearly)
        {
            if (!schedule.DeductionMonths.Contains(month))
            {
                return 0;
            }

            return BandFor(schedule, halfYearIncome ?? monthlyGross * 6)?.Amount ?? 0;
        }

        var band = BandFor(schedule, monthlyGross);
        if (band is null)
        {
            return 0;
        }

        return month == 2 && band.FebruaryAmount is { } february ? february : band.Amount;
    }

    /// <summary>
    /// The months of the half-year that ends in <paramref name="month"/> (April–September or October–March), in
    /// chronological order as (year, month) pairs. Used to add up income for half-yearly states.
    /// </summary>
    public static IReadOnlyList<(int Year, int Month)> HalfYearMonths(int year, int month)
    {
        var (startYear, startMonth) = month is >= 4 and <= 9 ? (year, 4) : month >= 10 ? (year, 10) : (year - 1, 10);
        var months = new List<(int, int)>(6);
        var current = new DateOnly(startYear, startMonth, 1);
        var end = new DateOnly(year, month, 1);
        while (current <= end)
        {
            months.Add((current.Year, current.Month));
            current = current.AddMonths(1);
        }

        return months;
    }

    /// <summary>Checks a slab table before it is saved: bands start at zero, rise strictly and are not negative.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<ProfessionalTaxBand> bands, ProfessionalTaxFrequency frequency, IReadOnlyList<int> deductionMonths)
    {
        var errors = new List<string>();
        if (bands.Count == 0)
        {
            errors.Add("Add at least one income band.");
            return errors;
        }

        var ordered = bands.OrderBy(b => b.IncomeFrom).ToList();
        if (ordered[0].IncomeFrom != 0)
        {
            errors.Add("The first band must start at ₹0.");
        }

        if (ordered.Select(b => b.IncomeFrom).Distinct().Count() != ordered.Count)
        {
            errors.Add("Two bands start at the same income.");
        }

        if (ordered.Any(b => b.Amount < 0 || b.FebruaryAmount < 0 || b.IncomeFrom < 0))
        {
            errors.Add("Amounts and incomes cannot be negative.");
        }

        if (frequency == ProfessionalTaxFrequency.HalfYearly && (deductionMonths.Count == 0 || deductionMonths.Any(m => m is < 1 or > 12)))
        {
            errors.Add("Choose the months (1–12) in which the half-yearly tax is deducted.");
        }

        return errors;
    }

    private static ProfessionalTaxBand? BandFor(ProfessionalTaxSchedule schedule, decimal income) =>
        schedule.Bands.Where(b => b.IncomeFrom <= income).MaxBy(b => b.IncomeFrom);
}
