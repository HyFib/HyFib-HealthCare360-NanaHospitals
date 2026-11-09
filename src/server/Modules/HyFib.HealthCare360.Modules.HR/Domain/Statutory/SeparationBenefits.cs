namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

public sealed record GratuityResult(bool Eligible, int CompletedYears, int RemainderDays, int QualifyingYears, decimal MonthlyWages, decimal Formula, decimal Amount,
    bool Capped, string Law, string Explanation);

/// <summary>
/// Gratuity on leaving: fifteen days' wages for every completed year of service, a day's wages being a month's wages ÷ 26,
/// and a part year over six months counting as a year (Payment of Gratuity Act 1972 s.4(2); Code on Social Security 2020
/// s.53(2), in force since 21 November 2025). It needs five years' continuous service except on death or disablement and,
/// under the Code, at the end of a fixed term, which is paid pro rata. Four years and 240 days worked in the fifth is
/// continuous service of five years (s.2A; the Code, s.54). The total is capped at ₹20 lakh (notified ceiling).
/// Wages are basic pay and dearness allowance under the Act, and wages as the Code defines them (with allowances above
/// half of pay added back) for anyone leaving on or after 21 November 2025.
/// </summary>
public static class Gratuity
{
    public const decimal Ceiling = 2_000_000m;
    public static readonly DateOnly CodeInForce = new(2025, 11, 21);

    /// <param name="daysWorkedInFinalYear">Days actually worked in the year after the last completed year; null counts the calendar days.</param>
    public static GratuityResult Compute(DateOnly joined, DateOnly lastWorkingDay, decimal monthlyWages, SeparationReason? reason, bool fixedTerm,
        int? daysWorkedInFinalYear = null)
    {
        var (years, remainder) = Service(joined, lastWorkingDay);
        var underCode = lastWorkingDay >= CodeInForce;
        var law = underCode ? "Code on Social Security 2020, s.53" : "Payment of Gratuity Act 1972, s.4";
        var worked = daysWorkedInFinalYear ?? remainder;
        var deathOrDisablement = reason == SeparationReason.Death;
        var fixedTermEnd = underCode && fixedTerm && reason == SeparationReason.EndOfContract;
        var fiveYears = years >= 5 || (years == 4 && worked >= 240);

        if (!fiveYears && !deathOrDisablement && !fixedTermEnd)
        {
            return new GratuityResult(false, years, remainder, 0, monthlyWages, 0, 0, false, law,
                $"{years} year(s) and {remainder} day(s) of service: gratuity needs five years' continuous service.");
        }

        var qualifying = years + (remainder > 182 ? 1 : 0);
        if (years == 4 && worked >= 240 && qualifying < 5)
        {
            qualifying = 5;
        }

        var formula = Money.Round(monthlyWages * 15m / 26m * qualifying);
        var amount = Math.Min(formula, Ceiling);
        return new GratuityResult(true, years, remainder, qualifying, monthlyWages, formula, amount, formula > Ceiling, law,
            $"₹{Money.FormatInr(monthlyWages)} × 15/26 × {qualifying} year(s)" + (formula > Ceiling ? ", capped at ₹20,00,000." : "."));
    }

    /// <summary>Completed years of service and the days after the last completed year (to and including the last working day).</summary>
    public static (int Years, int Days) Service(DateOnly joined, DateOnly lastWorkingDay)
    {
        if (lastWorkingDay < joined)
        {
            return (0, 0);
        }

        var end = lastWorkingDay.AddDays(1);
        var years = end.Year - joined.Year;
        if (joined.AddYears(years) > end)
        {
            years--;
        }

        return (years, end.DayNumber - joined.AddYears(years).DayNumber);
    }
}

public sealed record BonusMonth(int Year, int Month, decimal Wages, decimal DaysWorked);

public sealed record BonusResult(bool Eligible, decimal DaysWorked, decimal BonusWages, decimal Rate, decimal Amount, string Explanation);

/// <summary>
/// Statutory bonus for an accounting year (Payment of Bonus Act 1965; Code on Wages 2019, Chapter IV, in force since 21
/// November 2025). An employee on wages of ₹21,000 a month or less who worked at least 30 days in the year is paid between
/// 8.33 % and 20 % of the wages earned, each month's wages being counted up to the higher of ₹7,000 and the minimum wage
/// (s.12; the Code, s.26). The rate is the employer's, from the allocable surplus, within those limits. Months on more than
/// ₹21,000 do not count. Dismissal for fraud, violence or theft takes the bonus away (s.9; the Code, s.29).
/// </summary>
public static class StatutoryBonus
{
    public const decimal MinimumRate = 8.33m;
    public const decimal MaximumRate = 20m;
    public const decimal EligibilityCeiling = 21_000m;
    public const decimal CalculationFloor = 7_000m;
    public const decimal MinimumDays = 30m;

    public static BonusResult Compute(IEnumerable<BonusMonth> months, decimal ratePercent, decimal minimumWage, bool disqualified = false)
    {
        ArgumentNullException.ThrowIfNull(months);
        var rate = Math.Clamp(ratePercent, MinimumRate, MaximumRate);
        var cap = Math.Max(CalculationFloor, minimumWage);
        var counted = months.Where(m => m.Wages > 0 && m.Wages <= EligibilityCeiling).ToList();
        var days = counted.Sum(m => m.DaysWorked);
        if (disqualified)
        {
            return new BonusResult(false, days, 0, rate, 0, "Dismissed for fraud, violence or theft: no bonus.");
        }

        if (days < MinimumDays)
        {
            return new BonusResult(false, days, 0, rate, 0, $"{days} day(s) worked on eligible wages: bonus needs at least 30.");
        }

        var wages = counted.Sum(m => Math.Min(m.Wages, cap));
        var amount = Math.Round(wages * rate / 100m, 0, MidpointRounding.AwayFromZero);
        return new BonusResult(true, days, Money.Round(wages), rate, amount, $"{rate}% of ₹{Money.FormatInr(wages)} (each month up to ₹{Money.FormatInr(cap)}).");
    }
}

public sealed record LeaveEncashmentResult(decimal Days, decimal DailyRate, decimal Amount, decimal Exempt, decimal Taxable);

/// <summary>
/// Earned leave paid out: days × the month's encashment wages ÷ the divisor (30 by default; some policies use 26). Paid on
/// leaving, it is exempt up to the least of ₹25 lakh, ten months' average salary and the amount itself (s.10(10AA)(ii),
/// limit from 1 April 2023), and taxable beyond.
/// </summary>
public static class LeaveEncashment
{
    public const decimal ExemptionLimit = 2_500_000m;

    public static LeaveEncashmentResult Compute(decimal days, decimal monthlyWages, decimal divisor = 30m, decimal? averageMonthlySalary = null, bool onLeaving = true)
    {
        var rate = divisor <= 0 ? 0 : Money.Round(monthlyWages / divisor);
        var amount = Money.Round(Math.Max(0, days) * rate);
        var exempt = onLeaving ? Math.Min(amount, Math.Min(ExemptionLimit, 10 * (averageMonthlySalary ?? monthlyWages))) : 0;
        return new LeaveEncashmentResult(Math.Max(0, days), rate, amount, Money.Round(exempt), Money.Round(amount - exempt));
    }
}

public sealed record SettlementLine(string Code, string Description, decimal Amount);

public sealed record SettlementRecovery(string Code, string Description, decimal Amount);

public sealed record FullAndFinalInput(
    DateOnly LastWorkingDay,
    int NoticeDaysRequired,
    int NoticeDaysServed,
    bool NoticeWaived,
    bool EmployerEndedWithoutNotice,
    decimal MonthlyGross,
    decimal UnpaidSalary,
    LeaveEncashmentResult LeaveEncashment,
    GratuityResult Gratuity,
    decimal BonusDue,
    IReadOnlyList<SettlementRecovery> Recoveries);

public sealed record FullAndFinalStatement(IReadOnlyList<SettlementLine> Earnings, IReadOnlyList<SettlementLine> Recoveries, decimal TotalEarnings, decimal TotalRecoveries,
    decimal NetPayable, int NoticeShortfallDays);

/// <summary>
/// Full and final settlement: salary not yet paid, leave encashment, gratuity and bonus due, less recoveries (advances,
/// loans, canteen, assets not returned). Notice not served is recovered at a month's gross ÷ 30 a day unless waived; when
/// the employer ends the employment without notice, the notice period is paid instead. Wages due on leaving are paid
/// within two working days (Code on Wages 2019, s.17(2)).
/// </summary>
public static class FullAndFinalSettlement
{
    public static FullAndFinalStatement Compute(FullAndFinalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var daily = Money.Round(input.MonthlyGross / 30m);
        var earnings = new List<SettlementLine>();
        var recoveries = new List<SettlementLine>();

        if (input.UnpaidSalary > 0)
        {
            earnings.Add(new("SALARY", "Salary for days worked and not yet paid", Money.Round(input.UnpaidSalary)));
        }

        if (input.LeaveEncashment.Amount > 0)
        {
            earnings.Add(new("LEAVE_ENC", $"Leave encashment: {input.LeaveEncashment.Days} day(s) at ₹{Money.FormatInr(input.LeaveEncashment.DailyRate)}", input.LeaveEncashment.Amount));
        }

        if (input.Gratuity.Amount > 0)
        {
            earnings.Add(new("GRATUITY", $"Gratuity: {input.Gratuity.Explanation}", input.Gratuity.Amount));
        }

        if (input.BonusDue > 0)
        {
            earnings.Add(new("BONUS", "Statutory bonus due", Money.Round(input.BonusDue)));
        }

        var shortfall = Math.Max(0, input.NoticeDaysRequired - input.NoticeDaysServed);
        if (input.EmployerEndedWithoutNotice && input.NoticeDaysRequired > 0)
        {
            earnings.Add(new("NOTICE_PAY", $"Pay in lieu of notice: {input.NoticeDaysRequired} day(s)", Money.Round(daily * input.NoticeDaysRequired)));
            shortfall = 0;
        }
        else if (shortfall > 0 && !input.NoticeWaived)
        {
            recoveries.Add(new("NOTICE_SHORT", $"Notice not served: {shortfall} day(s) at ₹{Money.FormatInr(daily)}", Money.Round(daily * shortfall)));
        }

        recoveries.AddRange(input.Recoveries.Where(r => r.Amount > 0).Select(r => new SettlementLine(r.Code, r.Description, Money.Round(r.Amount))));
        var totalEarnings = earnings.Sum(e => e.Amount);
        var totalRecoveries = recoveries.Sum(r => r.Amount);
        return new FullAndFinalStatement(earnings, recoveries, totalEarnings, totalRecoveries, totalEarnings - totalRecoveries, input.NoticeWaived ? 0 : shortfall);
    }
}
