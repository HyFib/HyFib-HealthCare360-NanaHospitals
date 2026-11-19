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

/// <summary>A registered buyer (B2B) a counter's invoice is made out to.</summary>
/// <param name="PlaceOfSupply">Where the goods were supplied, as its GST state code ("33"); null when not recorded.</param>
public sealed record CounterBuyer(string Gstin, string? LegalName, string? PlaceOfSupply);

/// <summary>Money taken for an invoice, or paid back on a return, in one mode, with the terminal's RRN or the UPI reference.</summary>
public sealed record CounterPayment(CounterPaymentMode Mode, decimal Amount, string? Reference = null);

/// <summary>One invoice of a counter, for a voucher of its own in the accounts.</summary>
/// <param name="At">When it was sold.</param>
/// <param name="PatientId">The registered patient it was sold to; null for a walk-in customer.</param>
/// <param name="CustomerName">The name given at the counter (a walk-in customer's).</param>
/// <param name="Buyer">The registered buyer of a B2B invoice; null otherwise.</param>
/// <param name="Tax">Taxable value and GST by rate.</param>
/// <param name="RoundOff">The invoice's rounding to the rupee.</param>
/// <param name="Payments">Money taken with the invoice, by mode; the rest went on the customer's account.</param>
public sealed record CounterInvoice(
    string Number, DateTimeOffset At, Guid? PatientId, string? CustomerName, CounterBuyer? Buyer,
    IReadOnlyList<CounterTaxLine> Tax, decimal RoundOff, IReadOnlyList<CounterPayment> Payments)
{
    public decimal Total => Tax.Sum(t => t.Total) + RoundOff;

    /// <summary>What was not taken in money: put on the customer's account. Derived, so the figures always add up.</summary>
    public decimal OnAccount => Total - Payments.Sum(p => p.Amount);
}

/// <summary>A return of a counter's invoice booked on the day: a credit note of its own.</summary>
/// <param name="Number">The return's number.</param>
/// <param name="InvoiceNumber">The invoice the goods were sold on.</param>
/// <param name="Tax">Taxable value and GST the return takes back, by rate.</param>
/// <param name="PaidBack">Money paid back, by mode; the rest was set against the invoice's balance due.</param>
public sealed record CounterReturn(
    string Number, DateTimeOffset At, string InvoiceNumber, Guid? PatientId, string? CustomerName, CounterBuyer? Buyer,
    IReadOnlyList<CounterTaxLine> Tax, IReadOnlyList<CounterPayment> PaidBack)
{
    public decimal Total => Tax.Sum(t => t.Total);

    /// <summary>What was set against the invoice's balance due. Derived, so the figures always add up.</summary>
    public decimal AgainstDue => Total - PaidBack.Sum(p => p.Amount);
}

/// <summary>An invoice's balance on account paid on the day, after the day it was sold or later that day.</summary>
public sealed record CounterCollection(string InvoiceNumber, Guid? PatientId, string? CustomerName, IReadOnlyList<CounterPayment> Payments)
{
    public decimal Amount => Payments.Sum(p => p.Amount);
}

/// <summary>A counter's day invoice by invoice: the invoices sold, the returns booked and the balances paid.</summary>
/// <param name="Counter">The source's <see cref="ICounterInvoiceSource.Counter"/>.</param>
public sealed record CounterInvoicesDay(
    string Counter, DateOnly Date, IReadOnlyList<CounterInvoice> Invoices, IReadOnlyList<CounterReturn> Returns, IReadOnlyList<CounterCollection> Collections)
{
    public bool IsEmpty => Invoices.Count == 0 && Returns.Count == 0 && Collections.All(c => c.Amount == 0);
}

/// <summary>
/// A counter that bills its own customers and can report its day invoice by invoice for the Tally day book (INT-TALLY,
/// T17: one voucher per invoice), beside the summary of <see cref="ICounterSalesSource"/>. The pharmacy implements it; the
/// canteen, which has no invoices to post one by one, does not. Read in the background, where no facility filter applies:
/// implementations filter by the facility given and use the hospital's (IST) calendar day, and leave out what their
/// <see cref="ICounterSalesSource"/> day leaves out, so both describe the same day.
/// </summary>
public interface ICounterInvoiceSource
{
    /// <summary>The counter's key, the same as its <see cref="ICounterSalesSource.Counter"/>.</summary>
    string Counter { get; }

    Task<CounterInvoicesDay> GetInvoicesAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken);
}

/// <summary>
/// A card or UPI payment taken at a counter that bills its own sales, or (<paramref name="IsRefund"/>) money it paid back by
/// card or UPI, as the card and UPI settlement match reads it: a settled transaction names the reference the cashier typed
/// (the terminal's RRN, or its approval code), or the one the terminal reported (<see cref="ICounterTerminal"/>).
/// </summary>
/// <param name="PaymentId">The counter's own id of the payment: a settled transaction matches it once.</param>
/// <param name="Document">The invoice or bill the payment was for, as the counter numbers it.</param>
/// <param name="Cancelled">The document was cancelled since: its money went back at the counter, but the terminal's still settles.</param>
/// <param name="ApprovalCode">
/// The approval code the hospital's terminal reported for a payment taken on it, whose <paramref name="Reference"/> is then
/// the terminal's RRN: a settled transaction without the RRN is matched by it.
/// </param>
public sealed record CounterCardPayment(
    Guid PaymentId, string Document, CounterPaymentMode Mode, decimal Amount, string? Reference, DateTimeOffset At, bool IsRefund, bool Cancelled,
    string? ApprovalCode = null);

/// <summary>
/// A counter that bills its own customers outside the hospital bill — the pharmacy and the canteen — reports its day for
/// the Tally day book (INT-TALLY), and its card and UPI payments for the card and UPI settlement match. The owning module
/// implements it and Billing reads every registered source, so Billing never references those modules. Both are read in
/// the background, where no facility filter applies: implementations filter by the facility given and use the hospital's
/// (IST) calendar day.
/// </summary>
public interface ICounterSalesSource
{
    /// <summary>Stable key: <c>pharmacy</c> or <c>canteen</c>. It names the counter's ledgers and voucher numbers in the day book.</summary>
    string Counter { get; }

    Task<CounterSalesDay> GetDayAsync(Guid facilityId, DateOnly date, CancellationToken cancellationToken);

    /// <summary>The counter's card and UPI payments, and card and UPI refunds, taken at or after <paramref name="from"/> and before <paramref name="to"/>.</summary>
    Task<IReadOnlyList<CounterCardPayment>> GetCardPaymentsAsync(Guid facilityId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}
