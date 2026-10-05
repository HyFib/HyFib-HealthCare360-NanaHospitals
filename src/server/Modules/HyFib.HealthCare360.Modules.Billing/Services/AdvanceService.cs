using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Realtime;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Payload of the <c>billing.appointment.prepayment</c> outbox message: a verified online appointment payment.</summary>
public sealed record AppointmentPrepayment(
    Guid AppointmentId,
    string AppointmentNumber,
    Guid PatientId,
    Guid FacilityId,
    decimal Amount,
    string? Provider,
    string GatewayOrderId,
    string GatewayPaymentId,
    DateTimeOffset PaidAt);

/// <summary>
/// Patient advances — the online prepayment of an appointment (OA-03). Scheduling only raises events; the money is
/// receipted here through the outbox (so a billing problem can never undo the patient's booking, and the receipt number
/// is allocated inside the dispatcher's transaction, gap-free). The advance is adjusted against the consultation bill
/// when the visit is billed, follows a rescheduled booking (it is keyed by the gateway payment), and becomes a refund
/// request — approved and paid like any refund, by default reversed on the gateway — when the booking is cancelled.
/// </summary>
public sealed class AdvanceService(
    HospitalDbContext db,
    ReceiptService receipts,
    RefundService refunds,
    IOutbox outbox,
    IRealtimePublisher realtime,
    ILogger<AdvanceService> logger)
{
    public const string PrepaymentMessage = "billing.appointment.prepayment";

    /// <summary>Longest reschedule chain followed (a booking moved more often than this is left to staff).</summary>
    private const int MaxReschedules = 20;

    /// <summary>Queues the prepayment in the caller's unit of work (the Scheduling transaction that verified it).</summary>
    internal void QueueSync(AppointmentPrepayment payment) => outbox.Enqueue(PrepaymentMessage, payment, payment.FacilityId);

    /// <summary>
    /// Outbox handler body: receipts the prepayment as a patient advance (idempotent on the gateway payment id) and, when
    /// the booking — after following any reschedules — ended up cancelled, requests a refund of what is left of it.
    /// Stages everything in the dispatcher's unit of work; allocations happen last, so a failure never burns a number.
    /// </summary>
    internal async Task SyncAppointmentAsync(AppointmentPrepayment payment, CancellationToken ct)
    {
        var advance = await FindAsync(payment.GatewayPaymentId, ct) ?? await RecordAsync(payment, ct);
        if (advance is null)
        {
            return;
        }

        var booking = await LatestBookingAsync(payment.AppointmentId, ct);
        if (booking is { Status: AppointmentStatus.Cancelled })
        {
            await RequestRefundAsync(advance, $"Prepaid appointment {booking.Number} was cancelled", ct);
        }
    }

    /// <summary>
    /// On finalizing an OP bill: adjusts the prepayment of the visit's appointment against it (as much as both allow).
    /// Runs inside the finalizing transaction; does not save.
    /// </summary>
    internal async Task ApplyVisitPrepaymentAsync(Bill bill, CancellationToken ct)
    {
        if (bill.Type != BillType.OutPatient || bill.VisitId is not { } visitId || !bill.IsOpenForPayment)
        {
            return;
        }

        var appointmentId = await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(v => v.Id == visitId)
            .Select(v => v.AppointmentId)
            .FirstOrDefaultAsync(ct);
        var paymentId = appointmentId is null
            ? null
            : await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(a => a.Id == appointmentId && a.PaymentStatus == AppointmentPaymentStatus.PaidOnline)
                .Select(a => a.GatewayPaymentId)
                .FirstOrDefaultAsync(ct);
        if (paymentId is null || await FindAsync(paymentId, ct) is not { } advance
            || advance.PatientId != bill.PatientId || advance.FacilityId != bill.FacilityId)
        {
            return;
        }

        var amount = Math.Min(advance.Balance - await refunds.OutstandingForAdvanceAsync(advance.Id, ct), bill.DueAmount);
        if (amount <= 0)
        {
            return;
        }

        var adjusted = await receipts.AdjustPatientAdvanceAsync(bill, advance, amount, ct);
        if (adjusted.IsFailure)
        {
            logger.LogWarning("Prepayment {ReceiptNumber} was not adjusted on bill {BillNumber}: {Error}", advance.ReceiptNumber, bill.BillNumber, adjusted.Error!.Message);
        }
    }

    /// <summary>Counter adjustment of a patient advance against a finalized OP bill of the same patient and facility.</summary>
    public async Task<Result<Guid>> AdjustAsync(Guid billId, AdjustAdvanceRequest request, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().FirstOrDefaultAsync(b => b.Id == billId, ct);
        if (bill is null)
        {
            return Error.NotFound(nameof(Bill), billId);
        }

        if (bill.Type != BillType.OutPatient)
        {
            return Error.Rule("billing.use_ip_settlement", "In-patient bills are settled from the IP account.");
        }

        var advance = await db.Set<PatientAdvance>().FirstOrDefaultAsync(a => a.Id == request.AdvanceId, ct);
        if (advance is null)
        {
            return Error.NotFound(nameof(PatientAdvance), request.AdvanceId);
        }

        if (advance.PatientId != bill.PatientId || advance.FacilityId != bill.FacilityId)
        {
            return Error.Rule("billing.advance_other_patient", "This advance belongs to another patient or facility.");
        }

        db.GuardVersion(bill, request.Version);
        var available = advance.Balance - await refunds.OutstandingForAdvanceAsync(advance.Id, ct);
        var amount = Money.Round(request.Amount ?? Math.Min(available, bill.DueAmount));
        if (amount <= 0 || amount > available)
        {
            return Error.Rule("billing.advance_insufficient", $"₹{Money.FormatInr(Math.Max(available, 0m))} of this advance is available.");
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var adjusted = await receipts.AdjustPatientAdvanceAsync(bill, advance, amount, ct);
        if (adjusted.IsFailure)
        {
            return adjusted.Error!;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await realtime.ToFacilityAsync(bill.FacilityId, "billing.bill.changed", new { bill.Id, bill.PatientId, bill.Status }, ct);
        return adjusted.Value.Id;
    }

    /// <summary>
    /// A cancelled OP bill gives the advance it consumed back to that advance (no money moves; a completed "advance
    /// restored" refund records it). Runs inside the cancelling transaction; does not save.
    /// </summary>
    internal async Task RestoreForCancelledBillAsync(Bill bill, CancellationToken ct)
    {
        var adjusted = await db.Set<Receipt>()
            .Where(r => r.BillId == bill.Id && r.AdvanceId != null && r.Purpose == ReceiptPurpose.BillPayment)
            .SelectMany(r => r.Payments.Where(p => p.Mode == PaymentMode.Advance), (r, p) => new { AdvanceId = r.AdvanceId!.Value, p.Amount })
            .ToListAsync(ct);
        var restored = await db.Set<Refund>()
            .Where(r => r.BillId == bill.Id && r.AdvanceId != null && r.Mode == PaymentMode.Advance && r.Status == RefundStatus.Paid)
            .Select(r => new { AdvanceId = r.AdvanceId!.Value, r.Amount })
            .ToListAsync(ct);

        foreach (var used in adjusted.GroupBy(a => a.AdvanceId))
        {
            var held = used.Sum(a => a.Amount) - restored.Where(r => r.AdvanceId == used.Key).Sum(r => r.Amount);
            var amount = Math.Min(held, bill.RefundableAmount);
            if (amount <= 0 || bill.ApplyRefund(amount).IsFailure)
            {
                continue;
            }

            var advance = await db.Set<PatientAdvance>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstAsync(a => a.Id == used.Key, ct);
            advance.ReverseAdjustment(amount);
            await refunds.StageAdvanceReturnAsync(bill, amount, $"Advance restored: bill {bill.BillNumber} cancelled", ct, advance.Id);
        }
    }

    /// <summary>
    /// Keeps money the gateway took as a patient advance when it cannot be applied where it was meant to go (the
    /// admission was closed, or the patient paid an older order of a booking). Staff then adjust it on a bill or refund
    /// it. Idempotent on the gateway payment id. Stages in the caller's transaction (the receipt number is allocated
    /// there); the caller saves.
    /// </summary>
    internal async Task<PatientAdvance> RecordOnlinePaymentAsync(
        Guid facilityId, Guid patientId, decimal amount, string gatewayPaymentId, string? gatewayOrderId, string? provider, string notes,
        DateTimeOffset receivedAt, Guid? appointmentId, string? appointmentNumber, CancellationToken ct)
    {
        if (await FindAsync(gatewayPaymentId, ct) is { } existing)
        {
            return existing;
        }

        var survivor = await SurvivingPatientAsync(patientId, ct)
            ?? throw new InvalidOperationException($"Patient {patientId} of online payment {gatewayPaymentId} was not found.");
        var receipt = await receipts.RecordPatientAdvanceAsync(facilityId, survivor, Money.Round(amount), gatewayPaymentId, gatewayOrderId, notes, receivedAt, ct);
        var advance = new PatientAdvance
        {
            FacilityId = facilityId,
            PatientId = survivor,
            ReceiptId = receipt.Id,
            ReceiptNumber = receipt.ReceiptNumber,
            AppointmentId = appointmentId,
            AppointmentNumber = appointmentNumber,
            Provider = provider,
            GatewayOrderId = gatewayOrderId ?? string.Empty,
            GatewayPaymentId = gatewayPaymentId,
            Amount = Money.Round(amount),
            ReceivedAt = receivedAt,
        };
        receipt.AdvanceId = advance.Id;
        db.Add(advance);
        return advance;
    }

    /// <summary>A patient's advances with what is left of each and any refund awaiting approval or payment.</summary>
    public async Task<IReadOnlyList<PatientAdvanceDto>> ListAsync(Guid patientId, bool openOnly, CancellationToken ct)
    {
        var advances = await db.Set<PatientAdvance>().AsNoTracking()
            .Where(a => a.PatientId == patientId)
            .OrderByDescending(a => a.ReceivedAt)
            .ToListAsync(ct);
        var ids = advances.Select(a => a.Id).ToList();
        var pending = (await db.Set<Refund>().AsNoTracking()
                .Where(r => r.AdvanceId != null && ids.Contains(r.AdvanceId.Value) && r.Purpose == RefundPurpose.Advance
                            && (r.Status == RefundStatus.Requested || r.Status == RefundStatus.Approved))
                .Select(r => new { AdvanceId = r.AdvanceId!.Value, r.Amount })
                .ToListAsync(ct))
            .GroupBy(r => r.AdvanceId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        return advances
            .Where(a => !openOnly || a.Balance > 0)
            .Select(a => new PatientAdvanceDto(
                a.Id, a.PatientId, a.ReceiptNumber, a.AppointmentId, a.AppointmentNumber, a.Provider, a.Amount, a.AdjustedAmount, a.RefundedAmount,
                a.Balance, pending.GetValueOrDefault(a.Id), a.ReceivedAt))
            .ToList();
    }

    private async Task<PatientAdvance?> FindAsync(string gatewayPaymentId, CancellationToken ct) =>
        db.Set<PatientAdvance>().Local.FirstOrDefault(a => a.GatewayPaymentId == gatewayPaymentId)
        ?? await db.Set<PatientAdvance>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(a => a.GatewayPaymentId == gatewayPaymentId, ct);

    private async Task<PatientAdvance?> RecordAsync(AppointmentPrepayment payment, CancellationToken ct)
    {
        var amount = Money.Round(payment.Amount);
        if (amount <= 0 || string.IsNullOrWhiteSpace(payment.GatewayPaymentId))
        {
            logger.LogWarning("Prepayment of appointment {AppointmentNumber} ignored: no amount or payment id", payment.AppointmentNumber);
            return null;
        }

        // Money was received: it is recorded against the surviving UHID even if the patient was merged meanwhile.
        var patientId = await SurvivingPatientAsync(payment.PatientId, ct)
            ?? throw new InvalidOperationException($"Patient {payment.PatientId} of prepaid appointment {payment.AppointmentNumber} was not found.");

        var receipt = await receipts.RecordPatientAdvanceAsync(
            payment.FacilityId, patientId, amount, payment.GatewayPaymentId, payment.GatewayOrderId,
            $"Online prepayment for appointment {payment.AppointmentNumber}", payment.PaidAt, ct);
        var advance = new PatientAdvance
        {
            FacilityId = payment.FacilityId,
            PatientId = patientId,
            ReceiptId = receipt.Id,
            ReceiptNumber = receipt.ReceiptNumber,
            AppointmentId = payment.AppointmentId,
            AppointmentNumber = payment.AppointmentNumber,
            Provider = payment.Provider,
            GatewayOrderId = payment.GatewayOrderId,
            GatewayPaymentId = payment.GatewayPaymentId,
            Amount = amount,
            ReceivedAt = payment.PaidAt,
        };
        receipt.AdvanceId = advance.Id;
        db.Add(advance);
        return advance;
    }

    private async Task RequestRefundAsync(PatientAdvance advance, string reason, CancellationToken ct)
    {
        var amount = advance.Balance - await refunds.OutstandingForAdvanceAsync(advance.Id, ct);
        if (amount <= 0)
        {
            return;
        }

        var refund = await refunds.StageRequestAsync(advance.FacilityId, RefundPurpose.Advance, advance.PatientId, null, null, amount, reason, ct, advanceId: advance.Id);
        await refunds.NotifyApproversAsync(refund, ct);
    }

    /// <summary>The booking a prepayment ended up on, following reschedules (the payment moves with the booking).</summary>
    private async Task<Appointment?> LatestBookingAsync(Guid appointmentId, CancellationToken ct)
    {
        var booking = await BookingAsync(appointmentId, ct);
        for (var hops = 0; hops < MaxReschedules && booking is { Status: AppointmentStatus.Rescheduled, RescheduledToId: { } next }; hops++)
        {
            booking = await BookingAsync(next, ct);
        }

        return booking;
    }

    private async Task<Appointment?> BookingAsync(Guid id, CancellationToken ct) =>
        db.Set<Appointment>().Local.FirstOrDefault(a => a.Id == id)
        ?? await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(a => a.Id == id, ct);

    private async Task<Guid?> SurvivingPatientAsync(Guid patientId, CancellationToken ct)
    {
        var current = patientId;
        for (var hops = 0; hops < 10; hops++)
        {
            var row = await db.Set<Patient>().IgnoreQueryFilters()
                .Where(p => p.Id == current)
                .Select(p => new { p.MergedIntoPatientId })
                .FirstOrDefaultAsync(ct);
            if (row is null)
            {
                return null;
            }

            if (row.MergedIntoPatientId is not { } next)
            {
                return current;
            }

            current = next;
        }

        return current;
    }
}
