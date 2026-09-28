using System.Globalization;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Simulator;

namespace HyFib.HealthCare360.Integrations.Pos;

/// <summary>
/// Training terminal: a pushed sale waits for the "customer" and is approved after a few seconds
/// (<see cref="PosSettings.SimulatorApproveAfterSeconds"/>), so the counter's poll/cancel flow behaves as with a real
/// terminal. Any terminal id is accepted.
/// </summary>
internal sealed class SimulatorPosTerminal(SimulatorInbox inbox, IntegrationTrace trace) : IPosProvider
{
    public string Key => ProviderKeys.Simulator;

    public async Task<PosTransactionResult> StartSaleAsync(PosSaleRequest request, PosSettings settings, PosSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (!PosTransactionIds.IsValidTerminalId(request.TerminalId) || request.Amount <= 0)
        {
            return PosTerminalRouter.Declined(string.Empty, "A terminal id and a positive amount are required.");
        }

        var id = "possim_" + Guid.CreateVersion7().ToString("N")[..20];
        await inbox.AddTransactionAsync(new SimulatorTransaction
        {
            Kind = SimulatorTransactionKind.PosSale,
            ExternalId = id,
            Amount = request.Amount,
            TerminalId = request.TerminalId,
            Reference = IntegrationTrace.Truncate(request.Reference, 128),
            Status = SimulatorTransactionStatus.AwaitingCustomer,
        }, cancellationToken);
        await trace.LogAsync(IntegrationNames.Pos, Key, "sale.upload", true, started, request.Reference,
            $"{{\"terminal\":\"{request.TerminalId}\",\"amount\":{request.Amount}}}", id);
        return new PosTransactionResult(id, PosTransactionStatus.AwaitingCustomer, null, null, null, "Waiting for the customer (simulator).");
    }

    public async Task<PosTransactionResult> GetStatusAsync(string transactionId, PosSettings settings, PosSecrets secrets, CancellationToken cancellationToken)
    {
        var approveAfter = TimeSpan.FromSeconds(Math.Clamp(settings.SimulatorApproveAfterSeconds, 0, 300));
        var transaction = await inbox.UpdateTransactionAsync(transactionId, (t, now) =>
        {
            if (t.Status != SimulatorTransactionStatus.AwaitingCustomer || now - t.CreatedAt < approveAfter)
            {
                return false;
            }

            t.Status = SimulatorTransactionStatus.Approved;
            t.CompletedAt = now;
            t.ApprovalCode = ((uint)t.Id.GetHashCode() % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            t.PaymentId = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)[^12..];
            return true;
        }, cancellationToken);

        return transaction is null ? PosTerminalRouter.Declined(transactionId, "Unknown simulator transaction.") : ToResult(transaction);
    }

    public async Task<PosTransactionResult> CancelAsync(string transactionId, PosSettings settings, PosSecrets secrets, CancellationToken cancellationToken)
    {
        var transaction = await inbox.UpdateTransactionAsync(transactionId, (t, now) =>
        {
            if (t.Status != SimulatorTransactionStatus.AwaitingCustomer)
            {
                return false;
            }

            t.Status = SimulatorTransactionStatus.Cancelled;
            t.CompletedAt = now;
            return true;
        }, cancellationToken);

        return transaction is null ? PosTerminalRouter.Declined(transactionId, "Unknown simulator transaction.") : ToResult(transaction);
    }

    private static PosTransactionResult ToResult(SimulatorTransaction transaction) => transaction.Status switch
    {
        SimulatorTransactionStatus.Approved => new PosTransactionResult(transaction.ExternalId, PosTransactionStatus.Approved, transaction.ApprovalCode, "1111", transaction.PaymentId, "Approved (simulator)."),
        SimulatorTransactionStatus.Cancelled => new PosTransactionResult(transaction.ExternalId, PosTransactionStatus.Cancelled, null, null, null, "Cancelled (simulator)."),
        _ => new PosTransactionResult(transaction.ExternalId, PosTransactionStatus.AwaitingCustomer, null, null, null, "Waiting for the customer (simulator)."),
    };
}
