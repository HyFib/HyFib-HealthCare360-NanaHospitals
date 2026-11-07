namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

/// <summary>One part of the money a cancellation or a return pays back: to the payment it came in by, in that payment's mode.</summary>
/// <param name="PaymentId">The payment the money goes back to.</param>
/// <param name="Reference">
/// That payment's reference (the card's RRN or approval code, the UPI's UTR): the acquirer reports a refund under the sale's
/// reference, and the card and UPI settlement matches the refund by it.
/// </param>
public sealed record RefundPart(Guid PaymentId, PaymentMode Mode, decimal Amount, string? Reference);

/// <summary>
/// Money paid back at the pharmacy goes back the way it came (maker-checker, slice 2): cash as cash from the till, a card
/// payment to the same card, a UPI payment by UPI. Cash is never paid out for money that came in by card or UPI, so the till
/// cannot be emptied against a card sale. A refund smaller than what is left to pay back (a return of part of the invoice)
/// is shared among the payments in proportion to what each has left, to the paisa: each share is rounded down and the paise
/// left over go, one each, to the largest remainders. A cancellation pays every payment back whole.
/// </summary>
public static class SaleRefunds
{
    /// <summary>
    /// What each payment of the invoice still has to pay back: what it brought, less the refunds made against it. Refunds
    /// made before refunds named their payment are taken off the last payments first.
    /// </summary>
    public static IReadOnlyList<(PharmacyPayment Payment, decimal Left)> Refundable(IEnumerable<PharmacyPayment> payments)
    {
        ArgumentNullException.ThrowIfNull(payments);
        var all = payments.ToList();
        // In the order they were taken; payments taken together at the counter (a split bill) cash first, then card, then UPI.
        var taken = all.Where(p => !p.IsRefund && p.Mode != PaymentMode.Credit).OrderBy(p => p.ReceivedAt).ThenBy(p => p.Mode).ThenBy(p => p.Id).ToList();
        var refunds = all.Where(p => p.IsRefund).ToList();
        var left = taken.Select(p => p.Amount - refunds.Where(r => r.RefundOfPaymentId == p.Id).Sum(r => r.Amount)).ToArray();
        var unnamed = refunds.Where(r => r.RefundOfPaymentId is null).Sum(r => r.Amount);
        for (var i = taken.Count - 1; i >= 0 && unnamed > 0; i--)
        {
            var off = Math.Min(Math.Max(left[i], 0m), unnamed);
            left[i] -= off;
            unnamed -= off;
        }

        return taken.Select((p, i) => (p, Math.Max(left[i], 0m))).ToList();
    }

    /// <summary>The parts <paramref name="amount"/> goes back in, at most what the payments have left; none for nothing.</summary>
    public static IReadOnlyList<RefundPart> Plan(IEnumerable<PharmacyPayment> payments, decimal amount)
    {
        var open = Refundable(payments).Where(p => p.Left > 0).ToList();
        var total = open.Sum(p => p.Left);
        amount = Math.Min(Money.Round(amount), total);
        if (amount <= 0)
        {
            return [];
        }

        // In paise, so the shares add up exactly: each rounded down, then the paise left over to the largest remainders
        // (the earlier payment first when two are equal).
        var paise = (long)(amount * 100);
        var exact = open.Select(p => p.Left * paise / total).ToArray();
        var shares = exact.Select(e => (long)Math.Floor(e)).ToArray();
        var over = paise - shares.Sum();
        foreach (var i in Enumerable.Range(0, open.Count).OrderByDescending(i => exact[i] - shares[i]).ThenBy(i => i))
        {
            if (over == 0)
            {
                break;
            }

            if (shares[i] < (long)(open[i].Left * 100))
            {
                shares[i]++;
                over--;
            }
        }

        return open.Select((p, i) => new RefundPart(p.Payment.Id, p.Payment.Mode, shares[i] / 100m, p.Payment.Reference))
            .Where(r => r.Amount > 0)
            .ToList();
    }

    /// <summary>How the money goes back, in words: "₹60.00 back to the card (RRN 627910123456), ₹40.00 in cash".</summary>
    public static string Words(IReadOnlyList<RefundPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Count == 0
            ? "Nothing is paid back"
            : string.Join(", ", parts.Select(p => p.Mode switch
            {
                PaymentMode.Card => $"₹{Money.FormatInr(p.Amount)} back to the card{Reference("RRN or approval code", p.Reference)}",
                PaymentMode.Upi => $"₹{Money.FormatInr(p.Amount)} back by UPI{Reference("UTR", p.Reference)}",
                _ => $"₹{Money.FormatInr(p.Amount)} in cash",
            }));
    }

    /// <summary>The split by mode in short, for a one-line summary: "₹60.00 card, ₹40.00 cash".</summary>
    public static string ByMode(IReadOnlyList<RefundPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return string.Join(", ", parts.GroupBy(p => p.Mode).OrderBy(g => g.Key).Select(g => $"₹{Money.FormatInr(g.Sum(p => p.Amount))} {ModeWord(g.Key)}"));
    }

    private static string ModeWord(PaymentMode mode) => mode switch
    {
        PaymentMode.Card => "card",
        PaymentMode.Upi => "UPI",
        _ => "cash",
    };

    private static string Reference(string name, string? reference) => string.IsNullOrWhiteSpace(reference) ? string.Empty : $" ({name} {reference})";
}
