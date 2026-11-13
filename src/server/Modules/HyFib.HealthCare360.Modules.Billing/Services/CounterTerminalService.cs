using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The card and UPI terminals of the counters that bill their own sales (<see cref="ICounterTerminal"/>): Billing's
/// terminal-sale lifecycle (<see cref="PosService"/>: push, poll, cancel, the background re-check and the exception for
/// money no record holds), with the approved sale recorded by the counter's own payment instead of a Billing receipt.
/// Only a counter that reports its card and UPI payments to the settlement match (<see cref="ICounterSalesSource"/>)
/// takes them on the terminal, so every payment it takes there is matched with the acquirer's settlement.
/// </summary>
internal sealed class CounterTerminalService(PosService pos, IEnumerable<ICounterSalesSource> counters) : ICounterTerminal
{
    public async Task<Result<CounterTerminalSale>> StartAsync(CounterTerminalStart request, CancellationToken cancellationToken)
    {
        if (Known(request.Counter) is { } unknown)
        {
            return unknown;
        }

        if (ToMode(request.Mode) is not { } mode)
        {
            return Error.Field("mode", "Terminal sales are card or UPI payments.");
        }

        if (request.Amount <= 0 || Money.Round(request.Amount) != request.Amount)
        {
            return Error.Field("amount", "Enter an amount greater than zero, with at most 2 decimal places.");
        }

        if (string.IsNullOrWhiteSpace(request.TerminalId))
        {
            return Error.Field("terminalId", "Enter the terminal id on its label.");
        }

        var sale = await pos.StartForCounterAsync(request.Counter, request.Amount, request.TerminalId, mode, request.PatientId, cancellationToken);
        return sale.IsFailure ? sale.Error! : ToSale(sale.Value);
    }

    public async Task<Result<CounterTerminalSale>> RefreshAsync(string counter, Guid id, CancellationToken cancellationToken)
    {
        var sale = await pos.RefreshSaleAsync(id, counter, cancellationToken);
        return sale.IsFailure ? sale.Error! : ToSale(sale.Value);
    }

    public async Task<Result<CounterTerminalSale>> CancelAsync(string counter, Guid id, CancellationToken cancellationToken)
    {
        var sale = await pos.CancelSaleAsync(id, counter, cancellationToken);
        return sale.IsFailure ? sale.Error! : ToSale(sale.Value);
    }

    public async Task<IReadOnlyList<CounterTerminalSale>> UnusedAsync(string counter, CancellationToken cancellationToken) =>
        (await pos.UnusedAsync(counter, cancellationToken)).Select(ToSale).ToList();

    public async Task<Result<CounterTerminalPayment>> UseAsync(CounterTerminalUse use, CancellationToken cancellationToken)
    {
        if (ToMode(use.Mode) is not { } mode)
        {
            return Error.Rule("pos.sale_mode", "A terminal payment is a card or UPI payment.");
        }

        var sale = await pos.UseAsync(use.Counter, use.TerminalSaleId, use.PaymentId, use.Document, mode, use.Amount, cancellationToken);
        return sale.IsFailure
            ? sale.Error!
            : new CounterTerminalPayment(sale.Value.Id, sale.Value.TransactionId, sale.Value.TerminalId, sale.Value.Rrn, sale.Value.ApprovalCode, sale.Value.CardLast4);
    }

    private Error? Known(string counter) =>
        counters.Any(c => c.Counter == counter)
            ? null
            : Error.Rule("pos.counter_unknown", $"The '{counter}' counter does not report its card and UPI payments for the settlement match, so it cannot take them on the terminal.");

    private static PaymentMode? ToMode(CounterPaymentMode mode) => mode switch
    {
        CounterPaymentMode.Card => PaymentMode.Card,
        CounterPaymentMode.Upi => PaymentMode.Upi,
        _ => null,
    };

    private static CounterTerminalSale ToSale(PosTransaction s) => new(
        s.Id, s.Owner ?? string.Empty, s.Provider, s.TerminalId, s.TransactionId, s.Amount, s.Mode == PaymentMode.Upi ? CounterPaymentMode.Upi : CounterPaymentMode.Card,
        s.Status, s.ApprovalCode, s.CardLast4, s.Rrn, s.Message, s.OwnerDocument, s.CreatedAt, s.CompletedAt);
}
