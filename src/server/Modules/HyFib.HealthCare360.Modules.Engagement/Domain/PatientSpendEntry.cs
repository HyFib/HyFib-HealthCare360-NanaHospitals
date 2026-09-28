namespace HyFib.HealthCare360.Modules.Engagement.Domain;

public enum SpendKind
{
    Billed = 0,
    Paid,

    /// <summary>A finalized bill was cancelled: its <see cref="Billed"/> row no longer counts.</summary>
    BillCancelled,

    /// <summary>Money returned to the patient: reduces what the patient has paid.</summary>
    Refunded,
}

/// <summary>
/// Read model of a patient's spend for the 360° view (CRM-04). Billing sits above Engagement in the module layers, so
/// the figures arrive through its <c>BillFinalized</c>, <c>BillCancelled</c>, <c>PaymentReceived</c> and <c>RefundPaid</c>
/// events. Rows are append-only —
/// concurrent bills never contend for a shared total row inside Billing's transaction.
/// </summary>
[NotAudited]
public sealed class PatientSpendEntry : Entity
{
    public Guid PatientId { get; set; }

    /// <summary>Facility that billed or collected (null for rows recorded before facilities were carried on the events).</summary>
    public Guid? FacilityId { get; set; }

    public SpendKind Kind { get; set; }

    /// <summary>Bill, receipt or refund id (unique per kind, so a replayed event is ignored).</summary>
    public Guid SourceId { get; set; }

    public string Reference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
