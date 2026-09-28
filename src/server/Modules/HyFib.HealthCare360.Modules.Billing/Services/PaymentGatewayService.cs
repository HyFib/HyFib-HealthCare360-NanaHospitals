using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Realtime;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

public enum WebhookOutcome
{
    Processed = 0,

    /// <summary>Valid signature, but nothing of ours to act on (unknown order, not captured yet).</summary>
    Ignored,

    InvalidSignature,
    NotConfigured,
}

/// <summary>
/// Payment-gateway orders and payment links (INT-PG, PP-03). The gateway's own status API is the source of truth: a
/// webhook (signature-verified) or a poll only tells us which order to check, so no amount or status is ever taken from
/// an inbound payload. A captured payment is recorded once, keyed on the gateway payment id.
/// </summary>
public sealed class PaymentGatewayService(
    HospitalDbContext db,
    ReceiptService receipts,
    INotificationService notifications,
    IServiceProvider services,
    IRealtimePublisher realtime,
    TimeProvider time,
    ILogger<PaymentGatewayService> logger)
{
    private const int MaxCandidates = 200;

    /// <summary>Creates a payment link for a bill's balance or an IP advance and sends it by SMS/WhatsApp.</summary>
    public async Task<Result<GatewayOrderDto>> CreateLinkAsync(CreatePaymentLinkRequest request, CancellationToken ct)
    {
        var target = await PaymentTargets.ResolveAsync(db, request.BillId, request.IpAccountId, request.Amount, ct);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var amount = Money.Round(request.Amount ?? target.Value.Due);
        var created = await CreateOrderAsync(target.Value, amount, GatewayOrderKind.PaymentLink, PaymentChannel.Staff, ct);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var order = created.Value;
        var patient = await ContactAsync(order.PatientId, ct);
        var channels = new List<NotificationChannel>();
        if (request.SendSms)
        {
            channels.Add(NotificationChannel.Sms);
        }

        if (request.SendWhatsApp)
        {
            channels.Add(NotificationChannel.WhatsApp);
        }

        if (order.LinkUrl is { } link && patient?.Phone is not null && channels.Count > 0)
        {
            notifications.Queue(
                "payment.link",
                new NotificationRecipient(patient.Phone, patient.Email, order.PatientId, patient.FullName),
                new Dictionary<string, string>
                {
                    ["name"] = patient.FullName,
                    ["amount"] = Money.FormatInr(order.Amount),
                    ["purpose"] = target.Value.Description,
                    ["link"] = link,
                },
                channels,
                TimeSpan.FromDays(2));
        }

        await db.SaveChangesAsync(ct);
        return ToDto(order);
    }

    /// <summary>Hosted-checkout order for the patient portal's "pay now".</summary>
    internal async Task<Result<GatewayOrder>> CreatePortalOrderAsync(Guid billId, CancellationToken ct)
    {
        var target = await PaymentTargets.ResolveAsync(db, billId, null, null, ct);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var created = await CreateOrderAsync(target.Value, target.Value.Due, GatewayOrderKind.CheckoutOrder, PaymentChannel.Portal, ct);
        if (created.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return created;
    }

    public async Task<Result<GatewayOrderDto>> RefreshAsync(Guid id, CancellationToken ct)
    {
        var order = await db.Set<GatewayOrder>().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null)
        {
            return Error.NotFound(nameof(GatewayOrder), id);
        }

        var synced = await SyncAsync(order, ct);
        if (synced.IsFailure)
        {
            return synced.Error!;
        }

        return ToDto(synced.Value);
    }

    /// <summary>Asks the gateway for the authoritative status and records a captured payment exactly once.</summary>
    internal async Task<Result<GatewayOrder>> SyncAsync(GatewayOrder order, CancellationToken ct)
    {
        if (order.Status == GatewayOrderStatus.Paid)
        {
            return order;
        }

        var gateway = services.GetService<IPaymentGateway>();
        if (gateway is null)
        {
            return Error.Rule("billing.gateway_not_configured", "No payment gateway is configured.");
        }

        PaymentStatusResult status;
        try
        {
            status = await gateway.GetStatusAsync(order.OrderId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the status of gateway order {OrderId}", order.OrderId);
            return Error.Rule("billing.gateway_unavailable", "The payment gateway could not be reached. Try again shortly.");
        }

        if (status is { Status: GatewayPaymentStatus.Captured, PaymentId: { } paymentId })
        {
            if (!await TryRecordCaptureAsync(order, paymentId, status.AmountPaid ?? order.Amount, status.Method, ct))
            {
                return await db.Set<GatewayOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().FirstAsync(o => o.Id == order.Id, ct);
            }
        }
        else if (status.Status == GatewayPaymentStatus.Failed)
        {
            order.MarkFailed(status.Error);
            await db.SaveChangesAsync(ct);
        }

        return order;
    }

    /// <summary>Signed webhook from the gateway (anonymous endpoint).</summary>
    public async Task<WebhookOutcome> HandleWebhookAsync(string payload, string? signature, CancellationToken ct)
    {
        var gateway = services.GetService<IPaymentGateway>();
        if (gateway is null)
        {
            return WebhookOutcome.NotConfigured;
        }

        if (string.IsNullOrWhiteSpace(signature) || !gateway.VerifySignature(payload, signature))
        {
            logger.LogWarning("Rejected a payment-gateway webhook with a missing or invalid signature");
            return WebhookOutcome.InvalidSignature;
        }

        var candidates = ExtractCandidates(payload);
        if (candidates.Count == 0)
        {
            return WebhookOutcome.Ignored;
        }

        var order = await db.Set<GatewayOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(o => candidates.Contains(o.OrderId) || candidates.Contains(o.MerchantReference), ct);
        if (order is null)
        {
            return WebhookOutcome.Ignored;
        }

        var synced = await SyncAsync(order, ct);
        return synced.IsSuccess && synced.Value.Status == GatewayOrderStatus.Paid ? WebhookOutcome.Processed : WebhookOutcome.Ignored;
    }

    /// <summary>
    /// Provider-neutral: the ids and references present in a signed payload (at most 200 strings, depth-limited). Only
    /// used to find which of our orders to re-check with the gateway.
    /// </summary>
    internal static IReadOnlyList<string> ExtractCandidates(string payload)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            Collect(document.RootElement, values, 0);
        }
        catch (JsonException)
        {
            return [];
        }

        return values.ToList();
    }

    internal static GatewayOrderDto ToDto(GatewayOrder o) => new(
        o.Id, o.Provider, o.Kind, o.OrderId, o.Amount, o.Currency, o.Status, o.LinkUrl, o.CheckoutKey, o.BillId, o.IpAccountId, o.ReceiptId, o.Channel,
        o.CreatedAt, o.PaidAt);

    private static void Collect(JsonElement element, HashSet<string> values, int depth)
    {
        if (depth > 16 || values.Count >= MaxCandidates)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, values, depth + 1);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, values, depth + 1);
                }

                break;

            case JsonValueKind.String when element.GetString() is { Length: > 3 and <= 128 } text:
                values.Add(text);
                break;
        }
    }

    private async Task<Result<GatewayOrder>> CreateOrderAsync(PaymentTarget target, decimal amount, GatewayOrderKind kind, PaymentChannel channel, CancellationToken ct)
    {
        var gateway = services.GetService<IPaymentGateway>();
        if (gateway is null)
        {
            return Error.Rule("billing.gateway_not_configured", "No payment gateway is configured. Collect the payment at the counter.");
        }

        if (amount <= 0)
        {
            return Error.Field("amount", "Enter an amount greater than zero.");
        }

        var patient = await ContactAsync(target.PatientId, ct);
        var order = new GatewayOrder
        {
            FacilityId = target.FacilityId,
            Kind = kind,
            Amount = amount,
            Purpose = target.Purpose,
            BillId = target.BillId,
            IpAccountId = target.IpAccountId,
            PatientId = target.PatientId,
            Channel = channel,
            Status = GatewayOrderStatus.Created,
        };
        order.MerchantReference = order.Id.ToString("N");

        var request = new CreatePaymentOrder(
            amount, "INR", order.MerchantReference, target.Description, patient?.FullName, patient?.Phone, patient?.Email,
            new Dictionary<string, string> { ["merchantReference"] = order.MerchantReference });
        PaymentOrder created;
        try
        {
            created = kind == GatewayOrderKind.PaymentLink
                ? await gateway.CreatePaymentLinkAsync(request, ct)
                : await gateway.CreateOrderAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not create a {Kind} with the payment gateway", kind);
            return Error.Rule("billing.gateway_unavailable", "The payment gateway could not be reached. Try again or collect the payment at the counter.");
        }

        order.Provider = created.Provider;
        order.OrderId = created.OrderId;
        order.Currency = created.Currency;
        order.LinkUrl = created.PaymentLinkUrl;
        order.CheckoutKey = created.CheckoutKey;
        db.Add(order);
        return order;
    }

    /// <summary>Records the receipt for a captured payment; false when a concurrent webhook/poll recorded it first.</summary>
    private async Task<bool> TryRecordCaptureAsync(GatewayOrder order, string paymentId, decimal amount, string? method, CancellationToken ct)
    {
        await using (var transaction = await db.BeginOwnedTransactionAsync(ct))
        {
            try
            {
                var receipt = await receipts.RecordExternalAsync(new ExternalPayment(
                    order.FacilityId, order.Purpose, order.PatientId, order.BillId, order.IpAccountId, PaymentMode.PaymentGateway, Money.Round(amount),
                    paymentId, method, null, ReceiptSource.PaymentGateway, null), ct);
                if (receipt.Error is { } error)
                {
                    logger.LogError("Captured gateway payment {PaymentId} could not be recorded: {Error}", paymentId, error.Message);
                    order.LastError = error.Message;
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return true;
                }

                order.MarkPaid(paymentId, Money.Round(amount), method, receipt.Value.Id, time.GetUtcNow());
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                logger.LogInformation(ex, "Gateway payment {PaymentId} was recorded by a concurrent request", paymentId);
                db.ChangeTracker.Clear();
                return false;
            }
        }

        await realtime.ToFacilityAsync(order.FacilityId, "billing.bill.changed", new { Id = order.BillId, order.PatientId }, ct);
        return true;
    }

    private async Task<Contact?> ContactAsync(Guid patientId, CancellationToken ct) =>
        await db.Set<Patient>().IgnoreQueryFilters()
            .Where(p => p.Id == patientId)
            .Select(p => new Contact(p.FullName, p.Phone, p.Email))
            .FirstOrDefaultAsync(ct);

    private sealed record Contact(string FullName, string? Phone, string? Email);
}
