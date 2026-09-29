namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;

/// <param name="HsnSac">HSN/SAC code printed on the bill; not used in the arithmetic.</param>
public sealed record BillLineInput(Guid MenuItemId, string ItemName, int Quantity, decimal UnitPrice, decimal GstRate, bool PriceIncludesGst, string HsnSac);

public sealed record BillLine(
    Guid MenuItemId,
    string ItemName,
    int Quantity,
    decimal UnitPrice,
    decimal GstRate,
    bool PriceIncludesGst,
    decimal TaxableAmount,
    decimal Cgst,
    decimal Sgst,
    decimal LineTotal,
    string HsnSac);

public sealed record BillTotals(
    IReadOnlyList<BillLine> Lines,
    decimal TaxableAmount,
    decimal Cgst,
    decimal Sgst,
    decimal TotalGst,
    decimal GrossAmount,
    decimal RoundOff,
    decimal NetAmount);

/// <summary>
/// Canteen GST arithmetic (Indian tax invoice rules): tax is worked out per line — backed out of GST-inclusive prices
/// (taxable = price × 100 ÷ (100 + rate)) or added to exclusive ones — rounded to the paisa, split equally into CGST and
/// SGST (CGST takes the odd paisa), and the bill total is rounded to the nearest rupee with a round-off line.
/// </summary>
public static class CanteenBillCalculator
{
    public static BillTotals Calculate(IReadOnlyList<BillLineInput> lines)
    {
        var billLines = lines.Select(CalculateLine).ToList();
        var gross = billLines.Sum(l => l.LineTotal);
        var net = Math.Round(gross, 0, MidpointRounding.AwayFromZero);
        var cgst = billLines.Sum(l => l.Cgst);
        var sgst = billLines.Sum(l => l.Sgst);
        return new BillTotals(billLines, billLines.Sum(l => l.TaxableAmount), cgst, sgst, cgst + sgst, gross, net - gross, net);
    }

    public static BillLine CalculateLine(BillLineInput line)
    {
        if (line.Quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(line), line.Quantity, "Quantity must be positive.");
        }

        var amount = Money.Round(line.UnitPrice * line.Quantity);
        decimal taxable, gst;
        if (line.PriceIncludesGst)
        {
            taxable = Money.Round(amount * 100m / (100m + line.GstRate));
            gst = amount - taxable;
        }
        else
        {
            taxable = amount;
            gst = Money.PercentOf(taxable, line.GstRate);
        }

        var (cgst, sgst) = Money.SplitGst(gst);
        return new BillLine(line.MenuItemId, line.ItemName, line.Quantity, line.UnitPrice, line.GstRate, line.PriceIncludesGst, taxable, cgst, sgst, taxable + gst,
            line.HsnSac);
    }
}
