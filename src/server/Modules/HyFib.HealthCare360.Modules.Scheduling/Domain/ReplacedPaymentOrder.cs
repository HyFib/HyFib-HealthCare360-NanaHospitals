namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

/// <summary>
/// A gateway order an appointment had before its current one (OA-03): the patient started the online payment again and
/// a new order replaced it. The gateway can still take money on it (the first checkout left open on another tab or
/// phone, a UPI request approved late), so it is remembered, and a payment on it is found by the gateway's webhook,
/// Billing's re-check and the settlement match. It pays a booking nothing else has paid; on a booking another payment
/// already paid, it is a second payment, recorded here, which Billing keeps as an advance and refunds once a second
/// person approves.
/// </summary>
[AuditedWithParent]
public sealed class ReplacedPaymentOrder : Entity, IFacilityScoped
{
    public Guid AppointmentId { get; set; }
    public Guid FacilityId { get; set; }

    /// <summary>The gateway the order was made with.</summary>
    public string? Provider { get; set; }

    public string OrderId { get; set; } = string.Empty;
    public DateTimeOffset ReplacedAt { get; set; }

    /// <summary>A second payment the gateway took on this order, once found; null while none is known.</summary>
    public string? GatewayPaymentId { get; set; }

    public decimal? AmountPaid { get; set; }

    /// <summary>When the gateway took the second payment.</summary>
    public DateTimeOffset? PaidAt { get; set; }
}

/// <summary>What a payment on a replaced order did to its booking.</summary>
public enum ReplacedOrderPayment
{
    /// <summary>The payment was recorded before; nothing changed.</summary>
    AlreadyRecorded,

    /// <summary>Nothing else had paid the booking: the payment is the booking's own.</summary>
    PaidTheBooking,

    /// <summary>Another payment had paid the booking: this one is a second payment, to be refunded.</summary>
    PaidTwice,
}
