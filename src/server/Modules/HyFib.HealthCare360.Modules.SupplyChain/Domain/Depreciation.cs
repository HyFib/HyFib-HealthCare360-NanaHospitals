namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public sealed record DepreciationYear(int Year, DateOnly From, DateOnly To, decimal Opening, decimal Depreciation, decimal Closing);

/// <summary>
/// Asset depreciation (AST: straight-line and written-down value), pure and unit-tested. Schedules run in whole
/// years from the depreciation start date; <see cref="BookValue"/> pro-rates the current year by days so reports
/// taken mid-year are fair. Book value never falls below the salvage value.
/// </summary>
public static class DepreciationCalculator
{
    /// <summary>Equal charge every year: (cost − salvage) ÷ useful life; the last year absorbs rounding.</summary>
    public static IReadOnlyList<DepreciationYear> StraightLine(decimal cost, decimal salvage, int usefulLifeYears, DateOnly start)
    {
        Validate(cost, salvage);
        if (usefulLifeYears <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(usefulLifeYears), "Useful life must be at least one year.");
        }

        var annual = Money.Round((cost - salvage) / usefulLifeYears);
        var rows = new List<DepreciationYear>(usefulLifeYears);
        var opening = cost;
        for (var year = 1; year <= usefulLifeYears; year++)
        {
            var charge = year == usefulLifeYears ? opening - salvage : Math.Min(annual, opening - salvage);
            rows.Add(Row(year, start, opening, charge));
            opening -= charge;
        }

        return rows;
    }

    /// <summary>
    /// Written-down value: a fixed percentage of the opening book value each year (Income-tax / Companies Act
    /// block method), capped so the book value never drops below salvage.
    /// </summary>
    public static IReadOnlyList<DepreciationYear> WrittenDownValue(decimal cost, decimal salvage, decimal ratePercent, int years, DateOnly start)
    {
        Validate(cost, salvage);
        if (ratePercent is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(ratePercent), "The WDV rate must be between 0 and 100%.");
        }

        var rows = new List<DepreciationYear>();
        var opening = cost;
        for (var year = 1; year <= Math.Max(years, 1) && opening > salvage; year++)
        {
            var charge = Math.Min(Money.PercentOf(opening, ratePercent), opening - salvage);
            rows.Add(Row(year, start, opening, charge));
            opening -= charge;
        }

        return rows;
    }

    /// <summary>WDV rate that writes cost down to salvage over the life: 1 − (salvage ÷ cost)^(1 ÷ life).</summary>
    public static decimal WdvRateFor(decimal cost, decimal salvage, int usefulLifeYears)
    {
        if (cost <= 0 || usefulLifeYears <= 0)
        {
            return 0;
        }

        var residual = salvage <= 0 ? 0.05 : (double)(salvage / cost);
        return Math.Round((decimal)((1 - Math.Pow(residual, 1.0 / usefulLifeYears)) * 100), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Book value on a date: cost before the start, the pro-rated position inside a year, salvage/last closing after.</summary>
    public static decimal BookValue(IReadOnlyList<DepreciationYear> schedule, decimal cost, DateOnly asOf)
    {
        if (schedule.Count == 0 || asOf < schedule[0].From)
        {
            return cost;
        }

        var year = schedule.FirstOrDefault(r => r.From <= asOf && asOf <= r.To);
        if (year is null)
        {
            return schedule[^1].Closing;
        }

        var daysInYear = year.To.DayNumber - year.From.DayNumber + 1;
        var elapsed = asOf.DayNumber - year.From.DayNumber + 1;
        return Money.Round(year.Opening - (year.Depreciation * elapsed / daysInYear));
    }

    private static DepreciationYear Row(int year, DateOnly start, decimal opening, decimal charge)
    {
        var from = start.AddYears(year - 1);
        return new DepreciationYear(year, from, start.AddYears(year).AddDays(-1), opening, charge, opening - charge);
    }

    private static void Validate(decimal cost, decimal salvage)
    {
        if (cost < 0 || salvage < 0 || salvage > cost)
        {
            throw new ArgumentOutOfRangeException(nameof(salvage), "Salvage value must be between zero and the cost.");
        }
    }
}
