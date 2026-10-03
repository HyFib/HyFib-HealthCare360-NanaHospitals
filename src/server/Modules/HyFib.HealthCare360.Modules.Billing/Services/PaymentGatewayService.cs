using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
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

    /// <summary>The gateway delivered an event that was already handled; it is acknowledged and not acted on again.</summary>
    Duplicate,
}

/// <summary>
/// Payment-gateway orders and payment links (INT-PG, PP-03). The gateway's own status API is the source of truth: a
/// webhook (signature-verified) or a poll only tells us which order to check, so no amount or status is ever taken from
/// an inbound payload. A captured payment is recorded once, keyed on the gateway payment id, and dated when the
/// gateway took it. A payment that cannot be recorded stays visible on its order and in the reconciliation exceptions.
/// </summary>
public sealed class PaymentGatewayService(
    HospitalDbContext db,
    ReceiptService receipts,
    INotificationService notifications,
    IServiceProvider services,
    IRealtimePublisher realtime,
    GatewayDiscrepancyService discrepancies,
    GatewayWebhookLog webhookLog,
    GatewayRefundTracker refundTracker,
    PrepaymentChecker prepayments,
    GatewaySettlementService settlements,
    TallyCorrections tally,
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

    /// <summary>
    /// Asks the gateway for the authoritative status: a captured payment is recorded exactly once (dated when the
    /// gateway took it); a failed, expired or cancelled link or checkout is marked so.
    /// </summary>
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
            status = await gateway.GetStatusAsync(order.OrderId, order.FacilityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the status of gateway order {OrderId}", order.OrderId);
            return Error.Rule("billing.gateway_unavailable", "The payment gateway could not be reached. Try again shortly.");
        }

        switch (status)
        {
            case { Status: GatewayPaymentStatus.Captured, PaymentId: { } paymentId }:
                if (!await TryRecordCaptureAsync(order, paymentId, status.AmountPaid ?? order.Amount, status.Method, status.PaidAt, ct))
                {
                    return await db.Set<GatewayOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().FirstAsync(o => o.Id == order.Id, ct);
                }

                break;

            case { Status: GatewayPaymentStatus.Failed }:
                order.MarkFailed(status.Error);
                await db.SaveChangesAsync(ct);
                break;

            case { Status: GatewayPaymentStatus.Expired }:
                order.MarkExpired(status.Error);
                await db.SaveChangesAsync(ct);
                break;

            case { Status: GatewayPaymentStatus.Cancelled }:
                order.MarkCancelled(status.Error);
                await db.SaveChangesAsync(ct);
                break;
        }

        return order;
    }

    /// <summary>
    /// Signed webhook from the gateway (anonymous endpoint). A facility may have its own merchant account and webhook
    /// secret, so the ids in the (not yet trusted) payload are used only to find which of our orders, prepaid bookings
    /// or refunds it is about, and the signature is then checked with that facility's account.
    /// <paramref name="facilityHint"/> (<c>?facility=</c> on the webhook URL) is used when the payload names none of
    /// them. Each verified event is kept by its id (<paramref name="eventId"/>, else a hash of the body), so a
    /// redelivery is acknowledged without being handled twice.
    /// </summary>
    public async Task<WebhookOutcome> HandleWebhookAsync(
        string payload, string? signature, string? eventId, string provider, Guid? facilityHint, CancellationToken ct)
    {
        var gateway = services.GetService<IPaymentGateway>();
        if (gateway is null)
        {
            return WebhookOutcome.NotConfigured;
        }

        var candidates = ExtractCandidates(payload);
        GatewayOrder? order = null;
        Appointment? booking = null;
        Refund? refund = null;
        if (candidates.Count > 0)
        {
            order = await db.Set<GatewayOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .FirstOrDefaultAsync(o => candidates.Contains(o.OrderId) || candidates.Contains(o.MerchantReference), ct);
            booking = order is not null
                ? null
                : await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                    .FirstOrDefaultAsync(a => a.GatewayOrderId != null && candidates.Contains(a.GatewayOrderId), ct);
            refund = await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .FirstOrDefaultAsync(r => r.GatewayRefundId != null && candidates.Contains(r.GatewayRefundId), ct);
        }

        var facilityId = order?.FacilityId ?? booking?.FacilityId ?? refund?.FacilityId ?? facilityHint;
        if (string.IsNullOrWhiteSpace(signature) || !gateway.VerifySignature(payload, signature, facilityId))
        {
            logger.LogWarning("Rejected a payment-gateway webhook with a missing or invalid signature");
            return WebhookOutcome.InvalidSignature;
        }

        var info = GatewayWebhookInfo.Read(payload);
        var logged = await webhookLog.BeginAsync(provider, GatewayWebhookInfo.EventIdOf(eventId, payload), info, facilityId, ct);
        if (logged is not { } eventRowId)
        {
            return WebhookOutcome.Duplicate;
        }

        var outcome = WebhookEventOutcome.Ignored;
        string? note = null;
        try
        {
            if (order is not null)
            {
                var synced = await SyncAsync(order, ct);
                outcome = synced.IsFailure ? WebhookEventOutcome.Failed
                    : synced.Value is { Status: GatewayOrderStatus.Created, GatewayPaymentId: not null } ? WebhookEventOutcome.Failed
                    : synced.Value.Status is not GatewayOrderStatus.Created ? WebhookEventOutcome.Processed
                    : WebhookEventOutcome.Ignored;
                note = synced.IsFailure ? synced.Error!.Message : synced.Value.LastError;
            }
            else if (booking is not null)
            {
                var confirmed = await prepayments.ConfirmAsync(booking, ct);
                outcome = confirmed.IsSuccess ? WebhookEventOutcome.Processed
                    : confirmed.Error!.Code == "payment.not_captured" ? WebhookEventOutcome.Ignored
                    : WebhookEventOutcome.Failed;
                note = confirmed.IsSuccess ? $"Prepaid booking {booking.Number} confirmed." : confirmed.Error!.Message;
            }

            if (refund is not null && await refundTracker.FollowAsync(refund, ct))
            {
                outcome = WebhookEventOutcome.Processed;
                note = $"Refund {refund.RefundNumber}: {refund.GatewayStatus}.";
            }

            if (outcome == WebhookEventOutcome.Ignored && info.SettlementId is not null)
            {
                note = "Settlements are read by the reconciliation job every hour.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The job checks the order, booking or refund again; the gateway is told the delivery was received.
            logger.LogError(ex, "Payment-gateway webhook {EventType} could not be handled", info.EventType);
            outcome = WebhookEventOutcome.Failed;
            note = "Could not be handled; the reconciliation job checks again.";
        }

        await webhookLog.CompleteAsync(eventRowId, outcome, note, ct);
        return outcome == WebhookEventOutcome.Processed ? WebhookOutcome.Processed : WebhookOutcome.Ignored;
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
        o.CreatedAt, o.PaidAt, o.LastError, o.GatewayPaymentId);

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
                ? await gateway.CreatePaymentLinkAsync(request, target.FacilityId, ct)
                : await gateway.CreateOrderAsync(request, target.FacilityId, ct);
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

    /// <summary>
    /// Records the receipt for a captured payment, dated when the gateway took it; false when a concurrent webhook/poll
    /// recorded it first. When the bill or admission refuses the payment, the attempt is rolled back (so its receipt
    /// number goes back to the series) and the failure is kept on its own: the payment and the reason stay on the order,
    /// an exception is opened for the accountants (who are alerted the first time), and the next check tries again.
    /// </summary>
    private async Task<bool> TryRecordCaptureAsync(GatewayOrder order, string paymentId, decimal amount, string? method, DateTimeOffset? paidAt, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var capturedAt = paidAt is { } at && at < now ? at : now;
        Receipt? recorded = null;
        string? refused = null;
        IReadOnlyList<(Guid FacilityId, DateOnly Date)> settledDays = [];
        await using (var transaction = await db.BeginOwnedTransactionAsync(ct))
        {
            try
            {
                var receipt = await receipts.RecordExternalAsync(new ExternalPayment(
                    order.FacilityId, order.Purpose, order.PatientId, order.BillId, order.IpAccountId, PaymentMode.PaymentGateway, Money.Round(amount),
                    paymentId, method, null, ReceiptSource.PaymentGateway, null, capturedAt), ct);
                if (receipt.Error is { } error)
                {
                    // Not committed: the transaction rolls back when it is disposed.
                    refused = error.Message;
                }
                else
                {
                    recorded = receipt.Value;
                    order.MarkPaid(paymentId, Money.Round(amount), method, recorded.Id, recorded.ReceivedAt);
                    await discrepancies.ClearAsync(DiscrepancyKind.UnrecordedPayment, paymentId, $"Receipt {recorded.ReceiptNumber} recorded.", ct);
                    await db.SaveChangesAsync(ct);

                    // The receipt exists now, so a transaction the gateway already settled for this payment matches it.
                    settledDays = await settlements.RematchPaymentAsync(paymentId, ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
            }
            catch (DbUpdateException ex)
            {
                logger.LogInformation(ex, "Gateway payment {PaymentId} was recorded by a concurrent request", paymentId);
                db.ChangeTracker.Clear();
                return false;
            }
        }

        if (refused is not null)
        {
            await KeepCaptureFailureAsync(order, paymentId, Money.Round(amount), method, capturedAt, refused, ct);
            return true;
        }

        await realtime.ToFacilityAsync(order.FacilityId, "billing.bill.changed", new { Id = order.BillId, order.PatientId }, ct);
        await tally.RepostIfPostedAsync(order.FacilityId, BillingTime.LocalDate(recorded!.ReceivedAt), ct);
        await settlements.RepostAsync(settledDays, ct);
        return true;
    }

    /// <summary>The gateway took money that HC360 could not record yet: kept on the order and in the exceptions.</summary>
    private async Task KeepCaptureFailureAsync(
        GatewayOrder order, string paymentId, decimal amount, string? method, DateTimeOffset capturedAt, string reason, CancellationToken ct)
    {
        logger.LogError("Captured gateway payment {PaymentId} could not be recorded: {Error}", paymentId, reason);
        order.RecordCaptureFailure(paymentId, amount, method, capturedAt, reason);
        var exception = await discrepancies.RaiseAsync(
            DiscrepancyKind.UnrecordedPayment, paymentId, order.FacilityId,
            $"₹{Money.FormatInr(amount)} was paid online ({paymentId}), but the receipt could not be recorded: {reason}",
            d =>
            {
                d.OrderId = order.OrderId;
                d.GatewayOrderRecordId = order.Id;
                d.BillId = order.BillId;
                d.IpAccountId = order.IpAccountId;
                d.GatewayAmount = amount;
                d.GatewayAt = capturedAt;
            },
            ct);
        var isNew = db.Entry(exception).State == EntityState.Added;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent check kept the same failure first; it alerts the accountants.
            logger.LogInformation(ex, "The failure to record gateway payment {PaymentId} was kept by a concurrent request", paymentId);
            db.ChangeTracker.Clear();
            return;
        }

        if (isNew)
        {
            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.GatewayReconcile, order.FacilityId, "Online payment not recorded",
                $"₹{Money.FormatInr(amount)} was paid online but no receipt could be recorded. Open Gateway reconciliation to record it.",
                "/billing/reconciliation", "billing", "warning", ct);
        }
    }

    private async Task<Contact?> ContactAsync(Guid patientId, CancellationToken ct) =>
        await db.Set<Patient>().IgnoreQueryFilters()
            .Where(p => p.Id == patientId)
            .Select(p => new Contact(p.FullName, p.Phone, p.Email))
            .FirstOrDefaultAsync(ct);

    private sealed record Contact(string FullName, string? Phone, string? Email);
}
