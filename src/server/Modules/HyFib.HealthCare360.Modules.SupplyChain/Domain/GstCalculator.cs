namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

/// <summary>GST split of an amount. Intra-state supplies carry CGST + SGST; inter-state supplies carry IGST.</summary>
public sealed record TaxBreakup(decimal Taxable, decimal Gst, decimal Cgst, decimal Sgst, decimal Igst)
{
    public decimal Total => Taxable + Gst;
}

/// <summary>Amounts of one priced line (purchase order, GRN or sale line).</summary>
public sealed record PricedLine(decimal Gross, decimal Discount, TaxBreakup Tax)
{
    public decimal Net => Tax.Total;
}

/// <summary>
/// Indian GST arithmetic in one place (unit-tested): purchases are priced exclusive of GST, retail medicine sales
/// inclusive of GST (MRP). Money is rounded to paise half-away-from-zero; invoices round the total to the rupee.
/// </summary>
public static class GstCalculator
{
    /// <summary>Notified GST slabs (including the 2025 rationalised 5%/18%/40% structure and legacy slabs).</summary>
    public static IReadOnlyList<decimal> StandardRates { get; } = [0m, 0.25m, 3m, 5m, 12m, 18m, 28m, 40m];

    public static bool IsStandardRate(decimal rate) => StandardRates.Contains(rate);

    /// <summary>GST on a taxable value that excludes tax (vendor invoices and purchase orders).</summary>
    public static TaxBreakup OnExclusive(decimal taxableAmount, decimal ratePercent, bool interState)
    {
        var taxable = Money.Round(taxableAmount);
        return Split(taxable, Money.PercentOf(taxable, ratePercent), interState);
    }

    /// <summary>GST contained in a tax-inclusive price such as MRP: taxable = amount × 100 ÷ (100 + rate).</summary>
    public static TaxBreakup FromInclusive(decimal inclusiveAmount, decimal ratePercent, bool interState = false)
    {
        var total = Money.Round(inclusiveAmount);
        var taxable = Money.Round(total * 100m / (100m + ratePercent));
        return Split(taxable, total - taxable, interState);
    }

    /// <summary>Purchase line: quantity × rate, less trade discount, plus GST on the discounted value.</summary>
    public static PricedLine PriceExclusive(decimal quantity, decimal rate, decimal discountPercent, decimal gstRate, bool interState)
    {
        var gross = Money.Round(quantity * rate);
        var discount = Money.PercentOf(gross, discountPercent);
        return new PricedLine(gross, discount, OnExclusive(gross - discount, gstRate, interState));
    }

    /// <summary>Retail line at MRP: quantity × unit MRP, less discount; GST is carved out of what remains.</summary>
    public static PricedLine PriceInclusive(decimal quantity, decimal unitMrp, decimal discountPercent, decimal gstRate, bool interState = false)
    {
        var gross = Money.Round(quantity * unitMrp);
        var discount = Money.PercentOf(gross, discountPercent);
        return new PricedLine(gross, discount, FromInclusive(gross - discount, gstRate, interState));
    }

    /// <summary>Rounds an invoice total to the nearest rupee; Indian invoices print the difference as "round off".</summary>
    public static (decimal Rounded, decimal RoundOff) RoundToRupee(decimal amount)
    {
        var rounded = Math.Round(amount, 0, MidpointRounding.AwayFromZero);
        return (rounded, rounded - amount);
    }

    private static TaxBreakup Split(decimal taxable, decimal gst, bool interState)
    {
        if (interState)
        {
            return new TaxBreakup(taxable, gst, 0m, 0m, gst);
        }

        var (cgst, sgst) = Money.SplitGst(gst);
        return new TaxBreakup(taxable, gst, cgst, sgst, 0m);
    }
}
