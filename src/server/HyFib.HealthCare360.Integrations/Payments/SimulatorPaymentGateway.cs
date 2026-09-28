using System.Security.Cryptography;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Simulator;

namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>
/// Random signing key for simulator payment signatures, created per server process so nobody outside this server
/// can forge a "paid" simulator payment (set <c>simulatorSecret</c> to make signatures stable across restarts).
/// </summary>
internal sealed class SimulatorSigningKey
{
    public string Secret { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

/// <summary>
/// Training gateway: orders and links are captured immediately (auto-captured), refunds succeed, and links point to a
/// reserved <c>.invalid</c> host so a link sent to a patient during training can never be opened by mistake.
/// </summary>
internal sealed class SimulatorPaymentGateway(SimulatorInbox inbox, SimulatorSigningKey signingKey, IntegrationTrace trace) : IPaymentProvider
{
    public string Key => ProviderKeys.Simulator;

    public Task<PaymentOrder> CreateOrderAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken) =>
        CreateAsync(request, SimulatorTransactionKind.PaymentOrder, "order_sim", cancellationToken);

    public Task<PaymentOrder> CreatePaymentLinkAsync(CreatePaymentOrder request, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken) =>
        CreateAsync(request, SimulatorTransactionKind.PaymentLink, "plink_sim", cancellationToken);

    public async Task<PaymentStatusResult> GetStatusAsync(string orderId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var transaction = await inbox.UpdateTransactionAsync(orderId, (_, _) => false, cancellationToken);
        if (transaction is null)
        {
            return new PaymentStatusResult(orderId, GatewayPaymentStatus.Failed, null, null, null, "Unknown simulator order.");
        }

        var status = transaction.Status switch
        {
            SimulatorTransactionStatus.Refunded => GatewayPaymentStatus.Refunded,
            SimulatorTransactionStatus.Captured => GatewayPaymentStatus.Captured,
            SimulatorTransactionStatus.Cancelled => GatewayPaymentStatus.Failed,
            _ => GatewayPaymentStatus.Pending,
        };
        return new PaymentStatusResult(orderId, status, transaction.PaymentId, transaction.Amount - transaction.RefundedAmount, "simulator", null);
    }

    public async Task<RefundResult> RefundAsync(string paymentId, decimal amount, string reason, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        string? error = null;
        await inbox.UpdateTransactionAsync(paymentId, (transaction, _) =>
        {
            if (transaction.PaymentId != paymentId || amount <= 0 || amount > transaction.Amount - transaction.RefundedAmount)
            {
                error = "The refund exceeds the simulated payment.";
                return false;
            }

            transaction.RefundedAmount += amount;
            if (transaction.RefundedAmount >= transaction.Amount)
            {
                transaction.Status = SimulatorTransactionStatus.Refunded;
            }

            return true;
        }, cancellationToken);

        var refundId = error is null ? "rfnd_sim" + Guid.CreateVersion7().ToString("N")[..16] : null;
        await trace.LogAsync(IntegrationNames.Payment, Key, "refund", error is null, started, paymentId, $"{{\"amount\":{amount}}}", refundId, error);
        return new RefundResult(error is null, refundId, error);
    }

    public bool VerifySignature(string payload, string signature, PaymentSecrets secrets) =>
        Signatures.VerifyHmacSha256Hex(string.IsNullOrWhiteSpace(secrets.SimulatorSecret) ? signingKey.Secret : secrets.SimulatorSecret, payload, signature);

    private async Task<PaymentOrder> CreateAsync(CreatePaymentOrder request, SimulatorTransactionKind kind, string prefix, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        _ = PaymentAmounts.ToPaise(request.Amount); // same amount rules as the real gateway
        var id = prefix + Guid.CreateVersion7().ToString("N")[..20];
        await inbox.AddTransactionAsync(new SimulatorTransaction
        {
            Kind = kind,
            ExternalId = id,
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "INR" : request.Currency.ToUpperInvariant(),
            Reference = IntegrationTrace.Truncate(request.Receipt, 128),
            Description = IntegrationTrace.Truncate(request.Description, 300),
            Status = SimulatorTransactionStatus.Captured,
            PaymentId = "pay_sim" + Guid.CreateVersion7().ToString("N")[..20],
        }, cancellationToken);

        await trace.LogAsync(IntegrationNames.Payment, Key, kind == SimulatorTransactionKind.PaymentLink ? "link.create" : "order.create", true, started,
            request.Receipt, $"{{\"amount\":{request.Amount}}}", id);
        var link = kind == SimulatorTransactionKind.PaymentLink ? $"https://pay.simulator.invalid/{id}" : null;
        return new PaymentOrder(Key, id, request.Amount, "INR", "simulator", link);
    }
}
