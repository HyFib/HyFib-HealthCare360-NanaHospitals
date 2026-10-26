using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

public enum PaymentMode
{
    Cash = 0,
    Card,
    Upi,

    /// <summary>Amount put on the patient's account (staff, corporate or trusted patients) — collected later.</summary>
    Credit,
}

public enum SaleStatus
{
    Paid = 0,
    PartiallyPaid,
    Credit,
    Cancelled,
}

/// <summary>One payment split at the counter (card/UPI carry the terminal or UPI reference).</summary>
public sealed record SalePayment(PaymentMode Mode, decimal Amount, string? Reference);

public sealed record SaleLinePrice(decimal Gross, decimal Discount, decimal Taxable, decimal Cgst, decimal Sgst, decimal Net, decimal Igst = 0);

public sealed record SaleTotals(decimal Gross, decimal Discount, decimal Taxable, decimal Cgst, decimal Sgst, decimal RoundOff, decimal Net, decimal Igst = 0);

public sealed record Settlement(decimal Paid, decimal Due, SaleStatus Status);

/// <summary>
/// Retail pricing for the pharmacy counter (pure, unit-tested). Medicines sell at MRP, which already includes
/// GST, so tax is carved out of the discounted MRP value and split CGST/SGST (<see cref="Money.SplitGst"/>);
/// the invoice total is rounded to the rupee with a printed round-off.
/// </summary>
public static class SalePricing
{
    /// <summary>A line at MRP; GST in it is IGST when the goods are delivered in another state (IGST Act s.10).</summary>
    public static SaleLinePrice PriceLine(decimal unitMrp, decimal quantity, decimal gstRate, decimal discountPercent, bool interState = false)
    {
        var priced = GstCalculator.PriceInclusive(quantity, unitMrp, discountPercent, gstRate, interState);
        return new SaleLinePrice(priced.Gross, priced.Discount, priced.Tax.Taxable, priced.Tax.Cgst, priced.Tax.Sgst, priced.Net, priced.Tax.Igst);
    }

    public static SaleTotals Total(IEnumerable<SaleLinePrice> lines)
    {
        var list = lines.ToList();
        var net = list.Sum(l => l.Net);
        var (rounded, roundOff) = GstCalculator.RoundToRupee(net);
        return new SaleTotals(list.Sum(l => l.Gross), list.Sum(l => l.Discount), list.Sum(l => l.Taxable), list.Sum(l => l.Cgst), list.Sum(l => l.Sgst),
            roundOff, rounded, list.Sum(l => l.Igst));
    }

    /// <summary>
    /// Validates the payment split for a bill: every amount positive, the split adding up exactly to the bill (the
    /// counter gives change outside the system), and credit only for registered patients.
    /// </summary>
    public static Result<Settlement> Settle(decimal payable, IReadOnlyList<SalePayment> payments, bool creditAllowed)
    {
        if (payable == 0 && payments.Count == 0)
        {
            return new Settlement(0, 0, SaleStatus.Paid);
        }

        if (payments.Count == 0)
        {
            return Error.Field("payments", "Record how the bill is paid.");
        }

        if (payments.Any(p => p.Amount <= 0))
        {
            return Error.Field("payments", "Each payment must be more than zero.");
        }

        if (!creditAllowed && payments.Any(p => p.Mode == PaymentMode.Credit))
        {
            return Error.Rule("payment.credit_not_allowed", "Credit is allowed only for registered patients.");
        }

        var total = payments.Sum(p => p.Amount);
        if (total != payable)
        {
            return Error.Rule("payment.mismatch", $"Payments of Rs.{Money.FormatInr(total)} must add up to the bill amount of Rs.{Money.FormatInr(payable)}.");
        }

        var paid = payments.Where(p => p.Mode != PaymentMode.Credit).Sum(p => p.Amount);
        return new Settlement(paid, payable - paid, StatusFor(paid, payable - paid));
    }

    public static SaleStatus StatusFor(decimal paid, decimal due) =>
        due <= 0 ? SaleStatus.Paid : paid <= 0 ? SaleStatus.Credit : SaleStatus.PartiallyPaid;

    /// <summary>
    /// Refund for returning part of a line. The last return of a line refunds exactly what is left, so repeated
    /// partial returns never drift by a paisa from what the patient paid.
    /// </summary>
    public static decimal RefundFor(decimal lineNet, decimal lineQuantity, decimal alreadyReturnedQuantity, decimal alreadyRefunded, decimal returnQuantity)
    {
        if (lineQuantity <= 0 || returnQuantity <= 0)
        {
            return 0;
        }

        return alreadyReturnedQuantity + returnQuantity >= lineQuantity
            ? lineNet - alreadyRefunded
            : Money.Round(lineNet * returnQuantity / lineQuantity);
    }
}
