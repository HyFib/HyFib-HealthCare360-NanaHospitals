using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Engagement.Services.Handlers;

/// <summary>Finalized bills feed the patient 360° spend (CRM-04).</summary>
internal sealed class BillFinalizedSpendHandler(HospitalDbContext db) : IDomainEventHandler<BillFinalized>
{
    public Task HandleAsync(BillFinalized bill, CancellationToken cancellationToken) =>
        SpendLedger.RecordAsync(db, SpendKind.Billed, bill.BillId, bill.BillNumber, bill.PatientId, bill.FacilityId, bill.NetAmount, bill.FinalizedAt, cancellationToken);
}

/// <summary>A cancelled bill no longer counts towards the patient's spend.</summary>
internal sealed class BillCancelledSpendHandler(HospitalDbContext db) : IDomainEventHandler<BillCancelled>
{
    public Task HandleAsync(BillCancelled bill, CancellationToken cancellationToken) =>
        SpendLedger.RecordAsync(db, SpendKind.BillCancelled, bill.BillId, bill.BillNumber, bill.PatientId, bill.FacilityId, bill.NetAmount, bill.CancelledAt, cancellationToken);
}

/// <summary>Receipts feed the patient 360° spend (CRM-04).</summary>
internal sealed class PaymentReceivedSpendHandler(HospitalDbContext db) : IDomainEventHandler<PaymentReceived>
{
    public Task HandleAsync(PaymentReceived payment, CancellationToken cancellationToken) =>
        SpendLedger.RecordAsync(db, SpendKind.Paid, payment.ReceiptId, payment.ReceiptNumber, payment.PatientId, payment.FacilityId, payment.Amount, payment.ReceivedAt, cancellationToken);
}

/// <summary>Refunds paid out reduce what the patient has paid.</summary>
internal sealed class RefundPaidSpendHandler(HospitalDbContext db) : IDomainEventHandler<RefundPaid>
{
    public Task HandleAsync(RefundPaid refund, CancellationToken cancellationToken) =>
        SpendLedger.RecordAsync(db, SpendKind.Refunded, refund.RefundId, refund.RefundNumber, refund.PatientId, refund.FacilityId, refund.Amount, refund.PaidAt, cancellationToken);
}

/// <summary>Append-only spend rows, one per bill or receipt; a replayed event is ignored.</summary>
internal static class SpendLedger
{
    private const int MaxReferenceLength = 40;

    public static async Task RecordAsync(
        HospitalDbContext db,
        SpendKind kind,
        Guid sourceId,
        string reference,
        Guid patientId,
        Guid? facilityId,
        decimal amount,
        DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        if (db.Set<PatientSpendEntry>().Local.Any(e => e.Kind == kind && e.SourceId == sourceId)
            || await db.Set<PatientSpendEntry>().AnyAsync(e => e.Kind == kind && e.SourceId == sourceId, ct))
        {
            return;
        }

        db.Add(new PatientSpendEntry
        {
            PatientId = patientId,
            FacilityId = facilityId,
            Kind = kind,
            SourceId = sourceId,
            Reference = reference.Length <= MaxReferenceLength ? reference : reference[..MaxReferenceLength],
            Amount = Money.Round(amount),
            OccurredAt = occurredAt,
        });
    }
}
