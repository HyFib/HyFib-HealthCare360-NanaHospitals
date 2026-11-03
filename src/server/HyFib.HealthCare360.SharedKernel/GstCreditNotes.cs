namespace HyFib.HealthCare360.SharedKernel;

/// <summary>The taxable value and the GST a credit takes off a supply: CGST and SGST within the state, IGST to another.</summary>
public sealed record GstShare(decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst)
{
    public decimal Gst => Cgst + Sgst + Igst;
}

/// <summary>
/// A GST credit note (CGST Act 2017 s.34): it lowers the taxable value and the GST of a supply already invoiced, line by
/// line at each line's own rate, so the GST credited is the GST that line was charged. It reduces the tax only until
/// 30 November after the end of the financial year (April to March) of the supply (s.34(2), as the Finance Act 2022 put
/// it); one issued later lowers what is owed but not the tax. A write-off of a due that cannot be recovered is not a credit
/// note: it reduces no GST.
/// </summary>
public static class GstCreditNotes
{
    /// <summary>The last day a credit note can reduce the GST of a supply invoiced on <paramref name="supplyDate"/>.</summary>
    public static DateOnly LastDay(DateOnly supplyDate) => new(supplyDate.Month >= 4 ? supplyDate.Year + 1 : supplyDate.Year, 11, 30);

    /// <summary>
    /// What a credit of <paramref name="amount"/> (GST included) takes off a line whose value is <paramref name="lineNet"/>,
    /// <paramref name="lineTaxable"/> of it taxable and the rest GST, after earlier credits took <paramref name="creditedBefore"/>
    /// off it. Each credit's share is the difference of the shares of the running totals before and after it, so a line's
    /// credits add up to its own taxable value and GST to the paisa, however the line is credited.
    /// </summary>
    public static GstShare Share(decimal lineNet, decimal lineTaxable, decimal creditedBefore, decimal amount, bool interState)
    {
        var after = creditedBefore + amount;
        var taxable = TaxableIn(lineNet, lineTaxable, after) - TaxableIn(lineNet, lineTaxable, creditedBefore);
        var gst = amount - taxable;
        if (interState)
        {
            return new GstShare(taxable, 0m, 0m, gst);
        }

        // CGST and SGST are halves of the GST of the running total, so the line's credits add up to its own CGST and SGST.
        var cgst = CgstIn(lineNet, lineTaxable, after) - CgstIn(lineNet, lineTaxable, creditedBefore);
        return new GstShare(taxable, cgst, gst - cgst, 0m);
    }

    private static decimal TaxableIn(decimal lineNet, decimal lineTaxable, decimal credited) =>
        lineNet <= 0m ? 0m : credited >= lineNet ? lineTaxable : Money.Round(lineTaxable * credited / lineNet);

    private static decimal CgstIn(decimal lineNet, decimal lineTaxable, decimal credited) =>
        Money.SplitGst(credited - TaxableIn(lineNet, lineTaxable, credited)).Cgst;
}
