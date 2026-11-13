using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>A card or UPI amount a counter that bills its own sales pushes to the hospital's terminal.</summary>
/// <param name="Counter">The counter taking the payment: its <see cref="ICounterSalesSource.Counter"/> key.</param>
/// <param name="TerminalId">The terminal wired to the counter, as its label names it.</param>
/// <param name="PatientId">The registered patient paying, if any: a walk-in customer has none.</param>
public sealed record CounterTerminalStart(string Counter, decimal Amount, string TerminalId, CounterPaymentMode Mode, Guid? PatientId = null);

/// <summary>A counter's card or UPI sale on the terminal, as the counter's screen follows it.</summary>
/// <param name="UsedFor">The invoice or bill the counter recorded the payment on; null while no payment records it.</param>
public sealed record CounterTerminalSale(
    Guid Id,
    string Counter,
    string Provider,
    string TerminalId,
    string TransactionId,
    decimal Amount,
    CounterPaymentMode Mode,
    PosTransactionStatus Status,
    string? ApprovalCode,
    string? CardLast4,
    string? Rrn,
    string? Message,
    string? UsedFor,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>The counter records an approved terminal sale as one of its payments.</summary>
/// <param name="PaymentId">The counter's own id of the payment that records the sale.</param>
/// <param name="Document">The invoice or bill the payment is on, as the counter numbers it.</param>
/// <param name="Mode">The payment's mode, which must be the sale's.</param>
/// <param name="Amount">The payment's amount, which must be the sale's.</param>
public sealed record CounterTerminalUse(string Counter, Guid TerminalSaleId, Guid PaymentId, string Document, CounterPaymentMode Mode, decimal Amount);

/// <summary>What the terminal reported of an approved sale, kept with the counter's payment: nothing is typed.</summary>
public sealed record CounterTerminalPayment(Guid TerminalSaleId, string TransactionId, string TerminalId, string? Rrn, string? ApprovalCode, string? CardLast4)
{
    /// <summary>The payment's reference, which the card and UPI settlement match reads: the RRN (for UPI its UTR), else the approval code.</summary>
    public string Reference => Rrn ?? ApprovalCode ?? (TransactionId.Length > 64 ? TransactionId[..64] : TransactionId);
}

/// <summary>
/// The hospital's card and UPI terminals for the counters that bill their own sales (the pharmacy, the canteen), as
/// Billing's counters use them (INT-POS): the amount is pushed to the terminal, the counter follows it until the
/// customer pays, and the approved sale is then recorded as one of the counter's payments, with the terminal's RRN,
/// approval code and card digits. Billing implements it and keeps every terminal sale: a sale the counter left open
/// is asked about again in the background, and one approved that no payment records is shown to the accountants.
/// </summary>
public interface ICounterTerminal
{
    /// <summary>Pushes the amount to the terminal (card or UPI); the signed-in facility's terminal settings apply.</summary>
    Task<Result<CounterTerminalSale>> StartAsync(CounterTerminalStart request, CancellationToken cancellationToken);

    /// <summary>The sale as the terminal has it now (the counter asks every few seconds while the customer pays).</summary>
    Task<Result<CounterTerminalSale>> RefreshAsync(string counter, Guid id, CancellationToken cancellationToken);

    /// <summary>Cancels the sale on the terminal; a sale the customer paid first stays approved.</summary>
    Task<Result<CounterTerminalSale>> CancelAsync(string counter, Guid id, CancellationToken cancellationToken);

    /// <summary>The counter's approved sales that no payment records yet, newest first: money taken on the terminal still to be put on an invoice.</summary>
    Task<IReadOnlyList<CounterTerminalSale>> UnusedAsync(string counter, CancellationToken cancellationToken);

    /// <summary>
    /// Marks the approved sale as recorded by the counter's payment and returns what the terminal reported of it. Staged in
    /// the caller's unit of work: the counter saves it with its invoice, so neither is kept without the other. Refused
    /// when the sale is another counter's, not approved, already recorded, or of another amount or mode.
    /// </summary>
    Task<Result<CounterTerminalPayment>> UseAsync(CounterTerminalUse use, CancellationToken cancellationToken);
}

/// <summary>Fallback until the Billing module registers the terminals.</summary>
internal sealed class NullCounterTerminal : ICounterTerminal
{
    private static readonly Error Unavailable =
        Error.Rule("pos.unavailable", "Card/UPI terminals are run by the Billing module, which is not installed. Record the payment with the slip details instead.");

    public Task<Result<CounterTerminalSale>> StartAsync(CounterTerminalStart request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<CounterTerminalSale>>(Unavailable);

    public Task<Result<CounterTerminalSale>> RefreshAsync(string counter, Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<Result<CounterTerminalSale>>(Unavailable);

    public Task<Result<CounterTerminalSale>> CancelAsync(string counter, Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<Result<CounterTerminalSale>>(Unavailable);

    public Task<IReadOnlyList<CounterTerminalSale>> UnusedAsync(string counter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CounterTerminalSale>>([]);

    public Task<Result<CounterTerminalPayment>> UseAsync(CounterTerminalUse use, CancellationToken cancellationToken) =>
        Task.FromResult<Result<CounterTerminalPayment>>(Unavailable);
}
