using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Online appointment prepayments the patient's own browser never confirmed (the page was closed after paying, or the
/// connection dropped), and money taken on an order a booking had before its current one. From a gateway webhook, the
/// re-check job or an accountant's "record the receipt", it asks the gateway through Scheduling, which confirms the
/// booking, and then receipts the advance at once instead of waiting for the outbox. Both steps are keyed on the gateway
/// payment id, so nothing is recorded twice.
/// </summary>
public sealed class PrepaymentChecker(
    HospitalDbContext db,
    AppointmentPaymentService payments,
    AdvanceService advances,
    GatewayDiscrepancyService discrepancies,
    GatewaySettlementService settlements,
    TallyCorrections tally)
{
    /// <summary>
    /// Confirms the booking's online payment if the gateway took it and records the advance. Returns the advance's
    /// receipt number, or the gateway's answer when it has not been paid. Saves.
    /// </summary>
    internal async Task<Result<string>> ConfirmAsync(Appointment appointment, CancellationToken ct)
    {
        if (appointment.PaymentStatus != AppointmentPaymentStatus.PaidOnline)
        {
            var verified = await payments.VerifyWithGatewayAsync(appointment, ct);
            if (verified.IsFailure)
            {
                return verified.Error!;
            }
        }

        if (appointment is not { GatewayPaymentId: { } paymentId, AmountPaid: { } amount, PaidAt: { } paidAt })
        {
            return Error.Rule("payment.not_captured", "The payment has not been completed.");
        }

        return await RecordAsync(
            new AppointmentPrepayment(
                appointment.Id, appointment.Number, appointment.PatientId, appointment.FacilityId, amount, appointment.PaymentProvider,
                appointment.GatewayOrderId ?? string.Empty, paymentId, paidAt),
            ct);
    }

    /// <summary>
    /// Asks the gateway about <paramref name="order"/>, an order the booking had before its current one, and records the
    /// money taken on it: as the booking's own payment when nothing else paid it, or as a second payment, kept as a
    /// patient advance with its refund asked for (a second person approves it). Says what was done; the gateway's answer
    /// when nothing was taken on it. Saves.
    /// </summary>
    internal async Task<Result<string>> ConfirmReplacedOrderAsync(Appointment booking, ReplacedPaymentOrder order, CancellationToken ct)
    {
        var taken = await payments.CheckReplacedOrderAsync(booking, order, ct);
        if (taken.IsFailure)
        {
            return taken.Error!;
        }

        if (taken.Value == ReplacedOrderPayment.PaidTheBooking)
        {
            var confirmed = await ConfirmAsync(booking, ct);
            return confirmed.IsFailure
                ? confirmed.Error!
                : $"Prepaid booking {booking.Number} confirmed by a payment on an earlier order; advance receipt {confirmed.Value} recorded.";
        }

        if (order is not { GatewayPaymentId: { } paymentId, AmountPaid: { } amount, PaidAt: { } paidAt })
        {
            // Taken as the booking's own payment by an earlier check: nothing is left on this order.
            return $"The payment on earlier order {order.OrderId} is booking {booking.Number}'s own.";
        }

        var receipt = await RecordAsync(
            new AppointmentPrepayment(
                booking.Id, booking.Number, booking.PatientId, booking.FacilityId, amount, order.Provider ?? booking.PaymentProvider,
                order.OrderId, paymentId, paidAt, SecondPayment: true),
            ct);
        return $"Booking {booking.Number} was paid twice: payment {paymentId} on earlier order {order.OrderId} is kept as advance receipt {receipt}, and its refund awaits approval.";
    }

    /// <summary>Receipts the payment as a patient advance at once, clears its exception and matches its settlement. Saves.</summary>
    private async Task<string> RecordAsync(AppointmentPrepayment payment, CancellationToken ct)
    {
        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        await advances.SyncAppointmentAsync(payment, ct);
        var recorded = db.Set<PatientAdvance>().Local.FirstOrDefault(a => a.GatewayPaymentId == payment.GatewayPaymentId);
        var advance = recorded
            ?? await db.Set<PatientAdvance>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .FirstOrDefaultAsync(a => a.GatewayPaymentId == payment.GatewayPaymentId, ct);
        await discrepancies.ClearAsync(
            DiscrepancyKind.UnrecordedPayment, payment.GatewayPaymentId,
            advance is null ? "The booking is paid." : $"Advance receipt {advance.ReceiptNumber} recorded.", ct);
        var isNew = recorded is not null && db.Entry(recorded).State == EntityState.Added;
        await db.SaveChangesAsync(ct);
        IReadOnlyList<(Guid FacilityId, DateOnly Date)> settledDays = [];
        if (isNew)
        {
            // The receipt exists now, so a settled transaction for this payment matches it.
            settledDays = await settlements.RematchPaymentAsync(payment.GatewayPaymentId, ct);
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        if (isNew)
        {
            // The advance is dated when the patient paid, which may be a day already posted to Tally.
            await tally.RepostIfPostedAsync(payment.FacilityId, BillingTime.LocalDate(payment.PaidAt), ct);
            await settlements.RepostAsync(settledDays, ct);
        }

        return advance?.ReceiptNumber ?? string.Empty;
    }
}
