using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Contracts;

// Cross-module domain events. They live in Platform so modules can react to each other without compile-time
// dependencies (a module references only lower layers). They are raised from aggregate methods and dispatched
// in-process before SaveChanges commits, so handlers join the same transaction (all-or-nothing).
// Handlers: implement IDomainEventHandler<TEvent> anywhere in a module assembly — they are discovered automatically.

public enum OrderPriority
{
    Routine = 0,
    Urgent,
    Stat,
}

/// <summary>
/// A clinician ordered a service (lab test, imaging, cardiology test, procedure, physiotherapy, dialysis, diet,
/// blood). Raised by Clinical; Diagnostics / SpecialtyCare / SupportServices create work items, Billing may price it.
/// </summary>
public sealed record ServiceOrderPlaced(
    Guid OrderItemId,
    Guid OrderId,
    string OrderNumber,
    Guid PatientId,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid FacilityId,
    Guid ServiceItemId,
    string ServiceCode,
    string ServiceName,
    ServiceCategory Category,
    OrderPriority Priority,
    Guid? OrderingPractitionerId,
    string? ClinicalNotes,
    DateTimeOffset OrderedAt,
    // Set for tests that are components of a health-checkup package: billed once under the package, never per line.
    Guid? PackageBookingId = null,
    // Units requested (e.g. blood units, physiotherapy sessions); 1 for ordinary tests.
    int Quantity = 1) : DomainEvent
{
    public bool IsPackageComponent => PackageBookingId is not null;
}

public sealed record ServiceOrderCancelled(Guid OrderItemId, Guid PatientId, string Reason) : DomainEvent;

public sealed record PrescriptionLine(
    Guid LineId,
    Guid? DrugItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string? Route,
    string Dose,
    string Frequency,
    int DurationDays,
    decimal Quantity,
    string? Instructions,
    bool IsControlledDrug);

/// <summary>A doctor signed an e-prescription (ERX-03). Pharmacy queues it for dispensing.</summary>
public sealed record PrescriptionSigned(
    Guid PrescriptionId,
    string PrescriptionNumber,
    Guid PatientId,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid FacilityId,
    Guid PractitionerId,
    bool IsInpatient,
    IReadOnlyList<PrescriptionLine> Lines,
    DateTimeOffset SignedAt) : DomainEvent;

public sealed record PrescriptionCancelled(Guid PrescriptionId, Guid PatientId, string Reason) : DomainEvent;

/// <summary>Raised by Inpatient on admission; Billing opens the IP account, Dietary/Pharmacy start ward workflows.</summary>
public sealed record PatientAdmitted(
    Guid AdmissionId,
    string AdmissionNumber,
    Guid PatientId,
    Guid VisitId,
    Guid FacilityId,
    Guid? WardId,
    Guid? BedId,
    Guid? BedServiceItemId,
    Guid? AdmittingPractitionerId,
    PayerType PayerType,
    DateTimeOffset AdmittedAt) : DomainEvent;

/// <summary>The patient moved to another bed, possibly in another ward; Billing prices later services for the new bed class.</summary>
public sealed record PatientTransferred(
    Guid AdmissionId,
    Guid PatientId,
    Guid? FromBedId,
    Guid ToBedId,
    Guid? ToBedServiceItemId,
    DateTimeOffset TransferredAt,
    Guid? ToWardId = null) : DomainEvent;

public sealed record DischargeInitiated(Guid AdmissionId, Guid PatientId, DateTimeOffset InitiatedAt) : DomainEvent;

public sealed record PatientDischarged(Guid AdmissionId, Guid PatientId, Guid VisitId, string DischargeType, DateTimeOffset DischargedAt) : DomainEvent;

/// <summary>
/// An admission made in error (or for a patient who never arrived) was cancelled. Inpatient cancels the admission's
/// visit in the same transaction, so Billing reacts through <c>VisitCancelled</c>; other modules close what they opened
/// for the admission (diet orders, ward indents, surgery bookings), as they do at discharge.
/// </summary>
public sealed record AdmissionCancelled(Guid AdmissionId, Guid PatientId, Guid VisitId, Guid FacilityId, string Reason, DateTimeOffset CancelledAt) : DomainEvent;

public enum DiagnosticKind
{
    Laboratory = 0,
    Radiology,
    Cardiology,
}

/// <summary>A verified lab/radiology/cardiology report was released (Engagement notifies the patient; EHR shows it).</summary>
public sealed record DiagnosticReportReleased(
    Guid ReportId,
    DiagnosticKind Kind,
    Guid PatientId,
    Guid? VisitId,
    Guid? OrderItemId,
    string Title,
    bool HasCriticalFindings,
    DateTimeOffset ReleasedAt) : DomainEvent;

public sealed record AppointmentBooked(
    Guid AppointmentId,
    Guid PatientId,
    Guid PractitionerId,
    DateTimeOffset StartsAt,
    string Channel,
    bool IsTelehealth) : DomainEvent;

public sealed record AppointmentCancelled(Guid AppointmentId, Guid PatientId, string? Reason) : DomainEvent;

public sealed record DispensedLine(Guid? PrescriptionLineId, Guid ItemId, string ItemName, string? BatchNumber, decimal Quantity);

/// <summary>Medication issued by OP/IP pharmacy (feeds the nursing medication administration record).</summary>
public sealed record MedicationDispensed(
    Guid DispenseId,
    Guid PatientId,
    Guid? AdmissionId,
    Guid? PrescriptionId,
    IReadOnlyList<DispensedLine> Lines,
    DateTimeOffset DispensedAt) : DomainEvent;

public sealed record PaymentReceived(
    Guid ReceiptId,
    string ReceiptNumber,
    Guid PatientId,
    Guid? BillId,
    Guid? VisitId,
    Guid? AdmissionId,
    decimal Amount,
    string Mode,
    DateTimeOffset ReceivedAt,
    Guid? FacilityId = null) : DomainEvent;

public sealed record BillFinalized(
    Guid BillId,
    string BillNumber,
    Guid PatientId,
    Guid? VisitId,
    Guid? AdmissionId,
    decimal NetAmount,
    DateTimeOffset FinalizedAt,
    Guid? FacilityId = null) : DomainEvent;

/// <summary>
/// A finalized bill was cancelled; whatever was paid on it becomes refundable. Billing reverses doctor-fee accruals in
/// the same transaction; Engagement drops the bill from the patient's spend.
/// </summary>
public sealed record BillCancelled(
    Guid BillId,
    string BillNumber,
    Guid FacilityId,
    Guid PatientId,
    decimal NetAmount,
    string Reason,
    DateTimeOffset CancelledAt) : DomainEvent;

/// <summary>Money went back to the patient (cash, card reversal, UPI, bank transfer, cheque or a gateway refund).</summary>
public sealed record RefundPaid(
    Guid RefundId,
    string RefundNumber,
    Guid FacilityId,
    Guid PatientId,
    Guid? BillId,
    decimal Amount,
    DateTimeOffset PaidAt) : DomainEvent;

/// <summary>
/// Someone replied STOP (<paramref name="OptedOut"/>) or START to the hospital's WhatsApp number. Raised by Integrations
/// once for each newly received reply that is the number's latest, so webhook redeliveries and older replies arriving
/// late raise nothing. The platform records the number's WhatsApp opt-out, which the notification pipeline honours;
/// Engagement updates the marketing consent of the patients registered with the number.
/// </summary>
/// <param name="Phone">The sender's mobile number in E.164.</param>
/// <param name="RepliedAt">When the person sent the reply (WhatsApp's timestamp, or the time it arrived when missing).</param>
/// <param name="ProviderMessageId">The reply's WhatsApp message id (wamid).</param>
/// <param name="FacilityId">The facility whose WhatsApp number received it, when the webhook names one.</param>
public sealed record WhatsAppOptOutChanged(
    string Phone,
    bool OptedOut,
    DateTimeOffset RepliedAt,
    string ProviderMessageId,
    Guid? FacilityId) : DomainEvent;
