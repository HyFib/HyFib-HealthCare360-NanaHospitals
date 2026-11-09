namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

/// <summary>A financial year's s.194J rates for fees for professional services (doctors' fees), with the source.</summary>
public sealed record ProfessionalFeeRates(int StartYear, decimal Threshold, decimal Rate, decimal NoPanRate, string SectionCode, string Source);

public sealed record ProfessionalFeeTdsResult(decimal Basis, decimal RatePercent, decimal Tds, bool ThresholdCrossed, bool WithoutPan, string? Remark, string Explanation);

/// <summary>
/// TDS on fees for professional services (s.194J(1)(b); s.393 of the Income-tax Act 2025 from April 2026): 10 % once the
/// fees credited to the payee in the financial year exceed the threshold (₹30,000 to March 2025, ₹50,000 from April 2025,
/// Finance Act 2025). When a credit takes the year's total over the threshold, tax is deducted on the whole year's fees
/// not yet taxed, not only on the credit that crossed it. A payee without a valid PAN suffers 20 % (s.206AA(1)), and the
/// return says so (remark "C"). A lower-deduction certificate (s.197) sets a lower rate, for payees with a PAN only.
/// </summary>
public static class ProfessionalFeeTds
{
    public static IReadOnlyList<ProfessionalFeeRates> Years { get; } =
    [
        new(2024, 30_000, 10m, 20m, "94J-B", "Income-tax Act 1961, s.194J(1)(b); threshold ₹30,000 (Finance Act 2020)."),
        new(2025, 50_000, 10m, 20m, "94J-B", "Finance Act 2025: s.194J threshold raised to ₹50,000 from 1 April 2025."),
        new(2026, 50_000, 10m, 20m, "94J-B", "Income-tax Act 2025, s.393 table: FY 2025-26 threshold and rate carried forward. Check against the Finance Act 2026."),
    ];

    public static ProfessionalFeeRates? Find(int fyStartYear) => Years.FirstOrDefault(y => y.StartYear == fyStartYear);

    /// <param name="earlierCredited">Fees credited to the payee earlier in the same financial year.</param>
    /// <param name="earlierTaxedBasis">Of those, the amount tax has already been deducted on.</param>
    /// <param name="certificateRate">A s.197 certificate's rate, if the payee holds one for this year.</param>
    public static ProfessionalFeeTdsResult Compute(ProfessionalFeeRates rates, decimal earlierCredited, decimal earlierTaxedBasis, decimal amount, string? pan,
        decimal? certificateRate = null)
    {
        ArgumentNullException.ThrowIfNull(rates);
        var hasPan = HrIdentifiers.IsValidPan(pan);
        var yearTotal = Math.Max(0, earlierCredited) + Math.Max(0, amount);
        if (yearTotal <= rates.Threshold)
        {
            return new ProfessionalFeeTdsResult(0, 0, 0, false, !hasPan, null,
                $"Fees this year ₹{Money.FormatInr(yearTotal)} are within the ₹{Money.FormatInr(rates.Threshold)} threshold: no tax deducted.");
        }

        var basis = Math.Max(0, yearTotal - Math.Max(0, earlierTaxedBasis));
        var rate = !hasPan ? rates.NoPanRate : certificateRate is { } lower && lower >= 0 && lower < rates.Rate ? lower : rates.Rate;
        var tds = Math.Round(basis * rate / 100m, 0, MidpointRounding.AwayFromZero);
        var remark = !hasPan ? "C" : certificateRate is not null && rate < rates.Rate ? "A" : null;
        var why = !hasPan
            ? $"No valid PAN: {rate}% under s.206AA on ₹{Money.FormatInr(basis)}."
            : $"{rate}% under s.194J on ₹{Money.FormatInr(basis)}" + (basis > amount ? ", the year's fees not taxed before the threshold was crossed." : ".");
        return new ProfessionalFeeTdsResult(basis, rate, tds, true, !hasPan, remark, why);
    }
}
