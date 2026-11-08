namespace HyFib.HealthCare360.Modules.Analytics.Domain;

/// <summary>GST amounts of a set of documents (lines or headers), all in rupees.</summary>
public sealed record GstAmounts(decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst)
{
    public static readonly GstAmounts Zero = new(0m, 0m, 0m, 0m);

    public decimal Gst => Cgst + Sgst + Igst;

    public GstAmounts Plus(GstAmounts other) => new(Taxable + other.Taxable, Cgst + other.Cgst, Sgst + other.Sgst, Igst + other.Igst);

    public GstAmounts Negate() => new(-Taxable, -Cgst, -Sgst, -Igst);
}

/// <summary>One check of the GST summary against the documents it was built from.</summary>
/// <param name="Source">What was compared, in words ("Hospital bills").</param>
/// <param name="Lines">The sum of the lines the summary is built from.</param>
/// <param name="Headers">The totals of the documents themselves.</param>
public sealed record GstReconciliation(string Source, GstAmounts Lines, GstAmounts Headers)
{
    /// <summary>Within a paisa per component counts as reconciled: each document rounds its own lines.</summary>
    public const decimal Tolerance = 0.01m;

    public decimal TaxableDifference => Lines.Taxable - Headers.Taxable;

    public decimal GstDifference => Lines.Gst - Headers.Gst;

    public bool Reconciles => Math.Abs(TaxableDifference) <= Tolerance && Math.Abs(GstDifference) <= Tolerance;
}

/// <summary>Claims of one insurer submitted in a period, by outcome (the inputs of the settlement ratio).</summary>
public sealed record ClaimOutcomes(int Settled, int Rejected, int Open, decimal SettledClaimed, decimal SettledAmount, decimal RejectedClaimed);

/// <summary>Arithmetic of the tax, payroll and claims reports (unit-tested with worked examples).</summary>
public static class StatutoryMath
{
    /// <summary>The ageing buckets of receivables and claims, in days: 0–30, 31–60, 61–90, 91–180, over 180.</summary>
    public static int AgeBucket(int days) => days switch
    {
        <= 30 => 0,
        <= 60 => 1,
        <= 90 => 2,
        <= 180 => 3,
        _ => 4,
    };

    public static readonly IReadOnlyList<string> AgeBucketLabels = ["0–30 days", "31–60 days", "61–90 days", "91–180 days", "Over 180 days"];

    /// <summary>
    /// What an open claim still waits for: claimed − settled − deducted, never below nothing. Claimed ₹1,00,000, settled
    /// ₹60,000 with ₹5,000 deducted: ₹35,000 outstanding.
    /// </summary>
    public static decimal ClaimOutstanding(decimal claimed, decimal settled, decimal deducted) => Math.Max(claimed - settled - deducted, 0m);

    /// <summary>
    /// Claim settlement ratio by number: settled claims ÷ decided claims (settled + rejected) × 100, one decimal. Claims
    /// still open are left out until they are decided. 45 settled and 5 rejected: 90.0%. None decided: no ratio.
    /// </summary>
    public static decimal? SettlementRatioByCount(ClaimOutcomes o) => KpiMath.Percent(o.Settled, o.Settled + o.Rejected);

    /// <summary>
    /// Claim settlement ratio by amount: money settled ÷ money claimed on decided claims × 100, one decimal. Settled claims
    /// of ₹9,00,000 paid ₹8,10,000 and rejected claims of ₹1,00,000: 8,10,000 ÷ 10,00,000 = 81.0%.
    /// </summary>
    public static decimal? SettlementRatioByAmount(ClaimOutcomes o) => KpiMath.Percent(o.SettledAmount, o.SettledClaimed + o.RejectedClaimed);

    /// <summary>
    /// The effective TDS rate: TDS ÷ the amount it was deducted from × 100, two decimals. ₹10,000 on ₹1,00,000: 10.00%.
    /// </summary>
    public static decimal? EffectiveRate(decimal tds, decimal amount) =>
        amount == 0m ? null : Math.Round(tds / amount * 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// What an employee costs the hospital in a month: gross earnings plus the employer's PF and ESI contributions. Gross
    /// ₹30,000, employer PF ₹1,800, employer ESI ₹0 (wages over the ESI ceiling): ₹31,800.
    /// </summary>
    public static decimal EmployerCost(decimal grossEarnings, decimal employerPf, decimal employerEsi) => grossEarnings + employerPf + employerEsi;

    /// <summary>
    /// The part of a pharmacy return that was taxable value, or GST, in the proportion of the sale line it returns:
    /// refund × part ÷ line net. A ₹112 refund of a ₹224 line with ₹200 taxable value returns ₹100 taxable value.
    /// </summary>
    public static decimal ReturnShare(decimal refund, decimal linePart, decimal lineNet) =>
        lineNet == 0m ? 0m : Math.Round(refund * linePart / lineNet, 2, MidpointRounding.AwayFromZero);

    /// <summary>A month (year × 12 + month − 1) as a number, so months compare and count as integers.</summary>
    public static int MonthNumber(int year, int month) => (year * 12) + month - 1;
}
