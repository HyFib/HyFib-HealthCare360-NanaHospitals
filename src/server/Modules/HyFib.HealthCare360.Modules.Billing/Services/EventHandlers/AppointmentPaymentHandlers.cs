using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;

// Online appointment prepayments (OA-03). Scheduling posts nothing to billing: these handlers only queue an outbox message
// in Scheduling's transaction, and AdvanceService receipts / refunds the money when the message is delivered.

/// <summary>A verified online prepayment becomes a patient advance.</summary>
internal sealed class AppointmentPaymentCapturedHandler(AdvanceService advances) : IDomainEventHandler<AppointmentPaymentCaptured>
{
    public Task HandleAsync(AppointmentPaymentCaptured captured, CancellationToken cancellationToken)
    {
        advances.QueueSync(new AppointmentPrepayment(
            captured.AppointmentId, captured.AppointmentNumber, captured.PatientId, captured.FacilityId, captured.Amount, captured.Provider,
            captured.GatewayOrderId, captured.GatewayPaymentId, captured.PaidAt));
        return Task.CompletedTask;
    }
}

/// <summary>
/// A cancelled prepaid booking is refunded. A rescheduled booking also raises AppointmentCancelled, but its payment moves
/// to the new booking, so only a booking whose own status is Cancelled is acted on.
/// </summary>
internal sealed class AppointmentCancelledRefundHandler(HospitalDbContext db, AdvanceService advances) : IDomainEventHandler<AppointmentCancelled>
{
    public async Task HandleAsync(AppointmentCancelled cancelled, CancellationToken cancellationToken)
    {
        var booking = db.Set<Appointment>().Local.FirstOrDefault(a => a.Id == cancelled.AppointmentId)
            ?? await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(a => a.Id == cancelled.AppointmentId, cancellationToken);
        if (booking is
            {
                Status: AppointmentStatus.Cancelled,
                PaymentStatus: AppointmentPaymentStatus.PaidOnline,
                GatewayPaymentId: { } paymentId,
                AmountPaid: { } amount,
                PaidAt: { } paidAt,
            })
        {
            advances.QueueSync(new AppointmentPrepayment(
                booking.Id, booking.Number, booking.PatientId, booking.FacilityId, amount, booking.PaymentProvider, booking.GatewayOrderId ?? string.Empty,
                paymentId, paidAt));
        }
    }
}

/// <summary>Delivers <c>billing.appointment.prepayment</c> messages (retried with back-off on failure).</summary>
internal sealed class AppointmentPrepaymentOutboxHandler(AdvanceService advances) : IOutboxHandler
{
    public string Type => AdvanceService.PrepaymentMessage;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        advances.SyncAppointmentAsync(message.Read<AppointmentPrepayment>(), cancellationToken);
}
