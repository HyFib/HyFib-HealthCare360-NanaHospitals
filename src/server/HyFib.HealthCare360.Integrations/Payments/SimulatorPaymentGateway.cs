using System.Security.Cryptography;
using System.Text;
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
/// Training gateway: orders and links are captured immediately (auto-captured), refunds succeed and are processed at
/// once, payments and refunds are settled like a real gateway's (see <see cref="SimulatorSettlements"/>), and links
/// point to a reserved <c>.invalid</c> host so a link sent to a patient during training can never be opened by mistake.
/// </summary>
internal sealed class SimulatorPaymentGateway(SimulatorInbox inbox, SimulatorSigningKey signingKey, IntegrationTrace trace, TimeProvider time) : IPaymentProvider
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
            SimulatorTransactionStatus.Cancelled => GatewayPaymentStatus.Cancelled,
            _ => GatewayPaymentStatus.Pending,
        };
        return new PaymentStatusResult(orderId, status, transaction.PaymentId, transaction.Amount - transaction.RefundedAmount, "simulator", null, transaction.CreatedAt);
    }

    /// <summary>
    /// Like Razorpay, a repeated request with the same idempotency key gets the refund already made: its id is derived
    /// from the key, and the inbox refunds once per id.
    /// </summary>
    public async Task<RefundResult> RefundAsync(
        string paymentId, decimal amount, string reason, string? idempotencyKey, PaymentSettings settings,
        PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var refundId = "rfnd_sim" + (idempotencyKey is null
            ? Guid.CreateVersion7().ToString("N")[..16]
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{paymentId}:{idempotencyKey}")))[..16]);
        var error = await inbox.RefundAsync(paymentId, amount, refundId, cancellationToken);
        await trace.LogAsync(IntegrationNames.Payment, Key, "refund", error is null, started, paymentId, $"{{\"amount\":{amount}}}", error is null ? refundId : null, error);
        return error is null ? new RefundResult(true, refundId, null) : new RefundResult(false, null, error);
    }

    /// <summary>Simulated refunds are processed at once. Ones made before refunds were kept count as processed too.</summary>
    public async Task<RefundStatusResult> GetRefundStatusAsync(string refundId, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var refund = await inbox.FindRefundAsync(refundId, cancellationToken);
        if (refund is not null)
        {
            return new RefundStatusResult(refundId, PaymentRefundStatus.Processed, refund.Amount, refund.Reference, "SIMARN" + refundId[^8..].ToUpperInvariant(), refund.CreatedAt, null);
        }

        return refundId.StartsWith("rfnd_sim", StringComparison.Ordinal)
            ? new RefundStatusResult(refundId, PaymentRefundStatus.Processed, null, null, null, null, null)
            : new RefundStatusResult(refundId, PaymentRefundStatus.Failed, null, null, null, null, "The simulator has no such refund.");
    }

    /// <summary>
    /// Runs the day's simulated settlement if it is due (see <see cref="SimulatorSettlements.Plan"/>), then reports every
    /// simulated settlement made on that day.
    /// </summary>
    public async Task<IReadOnlyList<PaymentSettlement>> GetSettlementsAsync(
        DateOnly settledOn, PaymentSettings settings, PaymentSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        if (SimulatorSettlements.Plan(settledOn, settings.SimulatorSettlementDays, time.GetUtcNow()) is { } run)
        {
            await inbox.SettleAsync(run.SettleAt, run.CoverBefore, "setl_sim" + Guid.CreateVersion7().ToString("N")[..16], cancellationToken);
        }

        var (from, to) = SimulatorSettlements.Day(settledOn);
        var settlements = SimulatorSettlements.Build(await inbox.SettledBetweenAsync(from, to, cancellationToken));
        var day = settledOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await trace.LogAsync(IntegrationNames.Payment, Key, "settlement.recon", true, started, day, $"{{\"day\":\"{day}\"}}",
            $"settlements={settlements.Count} items={settlements.Sum(s => s.Items.Count)}");
        return settlements;
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
