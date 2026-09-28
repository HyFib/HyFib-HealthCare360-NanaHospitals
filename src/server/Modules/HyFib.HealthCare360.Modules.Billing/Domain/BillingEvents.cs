namespace HyFib.HealthCare360.Modules.Billing.Domain;

// Billing-internal domain events. Cross-module events (BillFinalized, BillCancelled, PaymentReceived, RefundPaid) live in
// Platform.Contracts.

/// <summary>
/// A payment settled the patient's share of a bill in full — by any route: counter, POS, gateway, IP or patient advance
/// adjustment — or the bill was born settled (payer covers it all). A credit note or write-off clearing the due does not
/// count: nothing was collected. Drives doctor-fee accrual for facilities that accrue on payment.
/// </summary>
public sealed record BillPaid(Guid BillId, Guid FacilityId) : DomainEvent;
