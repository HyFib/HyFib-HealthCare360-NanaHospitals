namespace HyFib.HealthCare360.Modules.Billing.Domain;

public sealed record ShareRuleView(
    Guid Id,
    Guid? PractitionerId,
    Guid? ServiceItemId,
    ServiceCategory? Category,
    PayerType? PayerType,
    ShareMethod Method,
    decimal Value,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo);

/// <summary>A billed line attributed to a doctor; <paramref name="BasisAmount"/> is its value after discounts, before GST.</summary>
public sealed record ShareLine(
    Guid PractitionerId,
    Guid ServiceItemId,
    ServiceCategory Category,
    PayerType PayerType,
    decimal Quantity,
    decimal BasisAmount,
    DateOnly ServiceDate);

public sealed record ShareResult(ShareRuleView? Rule, decimal Amount);

/// <summary>
/// Doctor share (DFA-01/02). Of the rules in force on the service date that match the line, the most specific wins:
/// a doctor-specific rule beats the hospital default, a service item beats a category, and a payer-type rule beats a
/// generic one; ties go to the most recent rule. Percentages apply to the billed value; fixed amounts are per unit and
/// never exceed the billed value, so a free follow-up earns nothing.
/// </summary>
public static class DoctorShareCalculator
{
    public static ShareRuleView? Match(IEnumerable<ShareRuleView> rules, ShareLine line) =>
        rules.Where(rule => Applies(rule, line))
            .OrderByDescending(Specificity)
            .ThenByDescending(rule => rule.EffectiveFrom)
            .FirstOrDefault();

    public static ShareResult Calculate(IEnumerable<ShareRuleView> rules, ShareLine line)
    {
        if (line.BasisAmount <= 0)
        {
            return new ShareResult(null, 0m);
        }

        var rule = Match(rules, line);
        if (rule is null)
        {
            return new ShareResult(null, 0m);
        }

        var amount = rule.Method == ShareMethod.Percentage
            ? Money.PercentOf(line.BasisAmount, Math.Clamp(rule.Value, 0m, 100m))
            : Math.Min(Money.Round(Math.Max(rule.Value, 0m) * Math.Max(line.Quantity, 0m)), line.BasisAmount);
        return new ShareResult(rule, amount);
    }

    private static bool Applies(ShareRuleView rule, ShareLine line) =>
        rule.EffectiveFrom <= line.ServiceDate
        && (rule.EffectiveTo is null || line.ServiceDate <= rule.EffectiveTo.Value)
        && (rule.PractitionerId is null || rule.PractitionerId == line.PractitionerId)
        && (rule.ServiceItemId is null || rule.ServiceItemId == line.ServiceItemId)
        && (rule.Category is null || rule.Category == line.Category)
        && (rule.PayerType is null || rule.PayerType == line.PayerType);

    private static int Specificity(ShareRuleView rule) =>
        (rule.PractitionerId is null ? 0 : 8) + (rule.ServiceItemId is null ? 0 : 4) + (rule.Category is null ? 0 : 2) + (rule.PayerType is null ? 0 : 1);
}

public sealed record PayoutAmounts(decimal Gross, decimal Deductions, decimal TdsPercent, decimal Tds, decimal Net);

/// <summary>
/// Payout arithmetic (DFA-04): TDS (section 194J) is deducted on the professional fee credited, i.e. the gross share;
/// recoveries and advances are deducted after tax.
/// </summary>
public static class PayoutCalculator
{
    public static PayoutAmounts Calculate(decimal gross, decimal deductions, decimal tdsPercent)
    {
        var grossAmount = Money.Round(gross);
        var deductionAmount = Money.Round(Math.Max(deductions, 0m));
        var percent = Math.Clamp(tdsPercent, 0m, 100m);
        var tds = grossAmount > 0 ? Money.PercentOf(grossAmount, percent) : 0m;
        return new PayoutAmounts(grossAmount, deductionAmount, percent, tds, grossAmount - tds - deductionAmount);
    }
}
