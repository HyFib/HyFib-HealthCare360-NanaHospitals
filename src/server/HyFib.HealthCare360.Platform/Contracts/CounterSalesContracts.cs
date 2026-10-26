namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>How money changed hands at a counter that bills its own sales.</summary>
public enum CounterPaymentMode
{
    Cash = 0,
    Card,
    Upi,
}

/// <summary>Taxable value and GST at one rate: CGST and SGST within the state, IGST on a supply to another state.</summary>
public sealed record CounterTaxLine(decimal GstRate, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst = 0)
{
    public decimal Total => Taxable + Cgst + Sgst + Igst;
}

public sealed record CounterMoney(CounterPaymentMode Mode, decimal Amount);

/// <summary>
/// One side of a counter's day: its invoices, or its returns and cancellations.
/// </summary>
/// <param name="Count">How many invoices (or returns) the figures cover.</param>
/// <param name="Tax">Taxable value and GST by rate.</param>
/// <param name="RoundOff">The invoices' rounding to the rupee.</param>
/// <param name="Payments">Money taken at the counter for them (or, for returns, paid back), by mode.</param>
public sealed record CounterDocuments(int Count, IReadOnlyList<CounterTaxLine> Tax, decimal RoundOff, IReadOnlyList<CounterMoney> Payments)
{
    public static CounterDocuments None { get; } = new(0, [], 0m, []);

    public decimal Total => Tax.Sum(t => t.Total) + RoundOff;

    /// <summary>
    /// What was not settled in money: put on a customer's account (pharmacy credit, canteen staff credit) or, for returns,
    /// set against a balance due. Derived, so the figures always add up.
    /// </summary>
    public decimal OnAccount => Total - Payments.Sum(p => p.Amount);
}

/// <summary>A counter's day for the accounts: its invoices, its returns and the balances customers paid later.</summary>
/// <param name="Counter">The source's <see cref="ICounterSalesSource.Counter"/>.</param>
/// <param name="Collected">Balances on account paid at the counter on the day, by mode.</param>
public sealed record CounterSalesDay(string Counter, DateOnly Date, CounterDocuments Sales, CounterDocuments Returns, IReadOnlyList<CounterMoney> Collected)
{
    public bool IsEmpty => Sales.Count == 0 && Returns.Count == 0 && Collected.All(c => c.Amount == 0);
}

/// <summary>
/// A counter that bills its own customers outside the hospital bill — the pharmacy and the canteen — reports its day for
/// the Tally day book (INT-TALLY). The owning module implements it and Billing reads every registered source, so Billing
/// never references those modules. The day book is built in the background, where no facility filter applies:
/// implementations filter by the facility given and use the hospital's (IST) calendar day.
/// </summary>
public interface ICounterSalesSource
{
    /// <summary>Stable key: <c>pharmacy</c> or <c>canteen</c>. It names the counter's ledgers and voucher numbers in the day book.</summary>
    string Counter { get; }

    Task<CounterSalesDay> GetDayAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken);
}
