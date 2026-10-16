using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>What the portal needs to open the payment gateway's checkout.</summary>
public sealed record PaymentCheckout(string Provider, string OrderId, decimal Amount, string Currency, string? CheckoutKey, string? PaymentLinkUrl);

/// <summary>
/// Optional online prepayment for portal bookings (OA-03, PP-03). The gateway is a Platform abstraction that may not
/// be configured (or may be unreachable while the hospital's internet is down): in that case the booking simply
/// becomes "pay at hospital" — a payment problem must never cost the patient their slot. A payment is accepted only
/// after the gateway signature ("order_id|payment_id", Razorpay convention) verifies. Nothing is posted to billing
/// here; <see cref="AppointmentPaymentCaptured"/> is raised for Billing to act on.
/// </summary>
public sealed class AppointmentPaymentService(
    HospitalDbContext db,
    IEnumerable<IPaymentGateway> gateways,
    TimeProvider time,
    ILogger<AppointmentPaymentService> logger)
{
    public const string Currency = "INR";

    /// <summary>The configured gateway (the last registration wins, as with any DI service), if any.</summary>
    private IPaymentGateway? Gateway { get; } = gateways.LastOrDefault();

    public bool IsAvailable => Gateway is not null;

    /// <summary>
    /// Creates a gateway order for the fee, or switches the booking to pay-at-hospital. With <paramref name="hosted"/> it
    /// creates a payment link instead, for when the portal cannot open the gateway's checkout (its script is blocked):
    /// the patient pays on the gateway's own page and the portal confirms it with <see cref="VerifyWithGatewayAsync"/>.
    /// The caller saves; before replacing an order the appointment already has, call <see cref="SettleOutstandingOrderAsync"/>.
    /// </summary>
    public async Task<PaymentCheckout?> StartCheckoutAsync(Appointment appointment, bool hosted, CancellationToken ct)
    {
        if (Gateway is null || appointment.FeeAmount <= 0 || !appointment.IsOpen)
        {
            appointment.ChoosePayAtHospital();
            return null;
        }

        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == appointment.PatientId)
            .Select(p => new { p.FullName, p.Phone, p.Email })
            .FirstOrDefaultAsync(ct);

        try
        {
            var request =
                new CreatePaymentOrder(
                    appointment.FeeAmount,
                    Currency,
                    appointment.Number,
                    $"Consultation fee {appointment.Number}",
                    patient?.FullName,
                    patient?.Phone,
                    patient?.Email,
                    new Dictionary<string, string> { ["appointmentId"] = appointment.Id.ToString(), ["module"] = "scheduling" });
            var order = hosted
                ? await Gateway.CreatePaymentLinkAsync(request, appointment.FacilityId, ct)
                : await Gateway.CreateOrderAsync(request, appointment.FacilityId, ct);

            var attached = appointment.AttachPaymentOrder(order.Provider, order.OrderId);
            if (attached.IsFailure)
            {
                return null;
            }

            return new PaymentCheckout(order.Provider, order.OrderId, order.Amount, order.Currency, order.CheckoutKey, order.PaymentLinkUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Payment gateway {Provider} could not create an order for appointment {AppointmentId}; switching to pay at hospital",
                Gateway.Provider, appointment.Id);
            appointment.ChoosePayAtHospital();
            return null;
        }
    }

    /// <summary>
    /// What became of the order the appointment already has, asked before a new one replaces it: an appointment keeps one
    /// order, so a replaced order that was paid after all would be found by neither the webhook nor the re-check, and the
    /// patient could pay twice. A captured order is recorded (the caller saves); one still being paid, or a gateway that
    /// cannot say, stops a new order. An order that was never paid may be replaced.
    /// </summary>
    public async Task<Result> SettleOutstandingOrderAsync(Appointment appointment, CancellationToken ct)
    {
        if (Gateway is null || appointment.GatewayOrderId is not { } orderId || appointment.PaymentStatus == AppointmentPaymentStatus.PaidOnline)
        {
            return Result.Success();
        }

        PaymentStatusResult status;
        try
        {
            status = await Gateway.GetStatusAsync(orderId, appointment.FacilityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the status of gateway order {OrderId} before replacing it for appointment {AppointmentId}", orderId, appointment.Id);
            return Error.Rule("payment.gateway_unavailable", "The payment gateway could not be reached. Try again shortly.");
        }

        return status switch
        {
            { Status: GatewayPaymentStatus.Captured, PaymentId: { } paymentId } =>
                appointment.MarkPaidOnline(orderId, paymentId, status.AmountPaid ?? appointment.FeeAmount, PaidAt(status, time.GetUtcNow())),
            { Status: GatewayPaymentStatus.Captured or GatewayPaymentStatus.Pending } =>
                Error.Rule("payment.in_progress", "A payment for this appointment is still being processed. Check its status again in a few minutes."),
            _ => Result.Success(),
        };
    }

    /// <summary>
    /// Verifies the checkout signature and records the payment. When the gateway can report the order status, a
    /// failed/unpaid order is rejected even if a signature was supplied. The caller saves.
    /// </summary>
    public async Task<Result> ConfirmAsync(Appointment appointment, string orderId, string paymentId, string signature, CancellationToken ct)
    {
        if (Gateway is null)
        {
            return Error.Rule("payment.unavailable", "Online payment is not available right now. Please pay at the hospital.");
        }

        if (!string.Equals(appointment.GatewayOrderId, orderId, StringComparison.Ordinal))
        {
            return Error.Rule("payment.order_mismatch", "This payment does not belong to this appointment.");
        }

        if (!Gateway.VerifySignature($"{orderId}|{paymentId}", signature, appointment.FacilityId))
        {
            logger.LogWarning("Rejected payment confirmation with an invalid signature for appointment {AppointmentId}", appointment.Id);
            return Error.Forbidden("payment.invalid_signature", "The payment could not be verified. If money was deducted, it will be refunded automatically.");
        }

        var amount = appointment.FeeAmount;
        var paidAt = time.GetUtcNow();
        try
        {
            var status = await Gateway.GetStatusAsync(orderId, appointment.FacilityId, ct);
            if (status.Status is not (GatewayPaymentStatus.Captured or GatewayPaymentStatus.Pending))
            {
                return Error.Rule("payment.not_captured", "The payment has not been completed. Please try again or pay at the hospital.");
            }

            amount = status.AmountPaid ?? amount;
            paidAt = PaidAt(status, paidAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The signature already proves the payment; the status call is a second opinion only.
            logger.LogInformation(ex, "Could not fetch gateway status for order {OrderId}; relying on the verified signature", orderId);
        }

        return appointment.MarkPaidOnline(orderId, paymentId, amount, paidAt);
    }

    /// <summary>
    /// Asks the gateway, server to server, whether the appointment's order was paid, and records the payment if so. A
    /// hosted payment page and the training gateway return no checkout signature, so the portal confirms those with this
    /// check. It is safe to repeat. The caller saves.
    /// </summary>
    public async Task<Result> VerifyWithGatewayAsync(Appointment appointment, CancellationToken ct)
    {
        if (appointment.PaymentStatus == AppointmentPaymentStatus.PaidOnline)
        {
            return Result.Success();
        }

        if (Gateway is null)
        {
            return Error.Rule("payment.unavailable", "Online payment is not available right now. Please pay at the hospital.");
        }

        if (appointment.GatewayOrderId is not { } orderId)
        {
            return Error.Rule("payment.not_started", "No online payment was started for this appointment.");
        }

        PaymentStatusResult status;
        try
        {
            status = await Gateway.GetStatusAsync(orderId, appointment.FacilityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the status of gateway order {OrderId} for appointment {AppointmentId}", orderId, appointment.Id);
            return Error.Rule("payment.gateway_unavailable", "The payment gateway could not be reached. Try again shortly.");
        }

        return status is { Status: GatewayPaymentStatus.Captured, PaymentId: { } paymentId }
            ? appointment.MarkPaidOnline(orderId, paymentId, status.AmountPaid ?? appointment.FeeAmount, PaidAt(status, time.GetUtcNow()))
            : Error.Rule("payment.not_captured", "The payment has not been completed. Please try again or pay at the hospital.");
    }

    /// <summary>When the gateway took the money (the advance receipt's time), never later than now.</summary>
    private static DateTimeOffset PaidAt(PaymentStatusResult status, DateTimeOffset now) =>
        status.PaidAt is { } paidAt && paidAt < now ? paidAt : now;
}
