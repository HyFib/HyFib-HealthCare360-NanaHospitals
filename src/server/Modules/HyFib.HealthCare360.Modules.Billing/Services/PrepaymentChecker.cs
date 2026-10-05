using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Online appointment prepayments the patient's own browser never confirmed (the page was closed after paying, or the
/// connection dropped). From a gateway webhook, the re-check job or an accountant's "record the receipt", it asks the
/// gateway through Scheduling, which confirms the booking, and then receipts the advance at once instead of waiting
/// for the outbox. Both steps are keyed on the gateway payment id, so nothing is recorded twice.
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

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        await advances.SyncAppointmentAsync(
            new AppointmentPrepayment(
                appointment.Id, appointment.Number, appointment.PatientId, appointment.FacilityId, amount, appointment.PaymentProvider,
                appointment.GatewayOrderId ?? string.Empty, paymentId, paidAt),
            ct);
        var recorded = db.Set<PatientAdvance>().Local.FirstOrDefault(a => a.GatewayPaymentId == paymentId);
        var advance = recorded
            ?? await db.Set<PatientAdvance>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .FirstOrDefaultAsync(a => a.GatewayPaymentId == paymentId, ct);
        await discrepancies.ClearAsync(
            DiscrepancyKind.UnrecordedPayment, paymentId, advance is null ? "The booking is paid." : $"Advance receipt {advance.ReceiptNumber} recorded.", ct);
        var isNew = recorded is not null && db.Entry(recorded).State == EntityState.Added;
        await db.SaveChangesAsync(ct);
        IReadOnlyList<(Guid FacilityId, DateOnly Date)> settledDays = [];
        if (isNew)
        {
            // The receipt exists now, so a settled transaction for this payment matches it.
            settledDays = await settlements.RematchPaymentAsync(paymentId, ct);
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        if (isNew)
        {
            // The advance is dated when the patient paid, which may be a day already posted to Tally.
            await tally.RepostIfPostedAsync(appointment.FacilityId, BillingTime.LocalDate(paidAt), ct);
            await settlements.RepostAsync(settledDays, ct);
        }

        return advance?.ReceiptNumber ?? string.Empty;
    }
}
