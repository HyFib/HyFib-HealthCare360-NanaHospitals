using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;

/// <summary>Reason for closing, cancelling or rejecting anything in the pharmacy.</summary>
public sealed record PharmacyReasonRequest(string Reason);

public sealed record QueueListItem(
    Guid Id,
    string PrescriptionNumber,
    Guid PatientId,
    string PatientName,
    string Uhid,
    Gender Gender,
    string? Age,
    string? PrescriberName,
    bool IsInpatient,
    QueueEntryStatus Status,
    int LineCount,
    int OpenLines,
    bool HasControlledDrugs,
    bool IsRefill,
    DateTimeOffset SignedAt,
    DateTimeOffset ReceivedAt,
    int WaitingMinutes);

/// <param name="Available">Sellable quantity of the prescribed item in the chosen store (when a store is given).</param>
public sealed record QueueLineDto(
    Guid Id,
    Guid PrescriptionLineId,
    Guid? DrugItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string? Route,
    string Dose,
    string Frequency,
    int DurationDays,
    string? Instructions,
    bool IsControlledDrug,
    decimal RequestedQuantity,
    decimal DispensedQuantity,
    decimal RemainingQuantity,
    QueueLineStatus Status,
    decimal? Available,
    decimal? Mrp,
    DateOnly? NearestExpiry);

public sealed record QueueEntryDetail(
    Guid Id,
    Guid PrescriptionId,
    string PrescriptionNumber,
    PatientSummary Patient,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid PractitionerId,
    string? PrescriberName,
    string? PrescriberRegistrationNumber,
    bool IsInpatient,
    QueueEntryStatus Status,
    DateTimeOffset SignedAt,
    DateTimeOffset ReceivedAt,
    string? CloseReason,
    int RefillsAuthorized,
    int RefillsUsed,
    DateOnly? RefillValidUntil,
    Guid? RefillOfEntryId,
    IReadOnlyList<QueueLineDto> Lines,
    uint Version);

/// <param name="ItemId">Item handed over — the prescribed one, or a generic equivalent (substitution).</param>
/// <param name="BatchId">Batch scanned at the counter; FEFO when omitted.</param>
/// <param name="PrescriberConsent">The prescriber agreed to the substitution (required for any substitution).</param>
public sealed record DispenseLineRequest(Guid QueueLineId, Guid ItemId, decimal Quantity, Guid? BatchId, string? SubstitutionReason, bool PrescriberConsent);

/// <param name="ConfirmControlledDrugs">Explicit confirmation for Schedule H1/X or narcotic medicines (ERX-06).</param>
public sealed record DispenseRequest(
    Guid StoreId,
    IReadOnlyList<DispenseLineRequest> Lines,
    string? CounsellingNotes,
    decimal DiscountPercent,
    IReadOnlyList<SalePayment> Payments,
    bool ConfirmControlledDrugs);

public sealed record AuthorizeRefillsRequest(int Refills, DateOnly ValidUntil, string Note);

public sealed class PharmacyReasonRequestValidator : AbstractValidator<PharmacyReasonRequest>
{
    public PharmacyReasonRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

/// <summary>One payment of a split bill; card and UPI need the slip / transaction reference for reconciliation.</summary>
public sealed class SalePaymentValidator : AbstractValidator<SalePayment>
{
    public SalePaymentValidator()
    {
        RuleFor(x => x.Amount).GreaterThan(0);
        RuleFor(x => x.Reference).MaximumLength(64);
        RuleFor(x => x.Reference).NotEmpty().When(x => x.Mode is PaymentMode.Card or PaymentMode.Upi)
            .WithMessage("Enter the card slip or UPI transaction reference.");
    }
}

public sealed class DispenseRequestValidator : AbstractValidator<DispenseRequest>
{
    public DispenseRequestValidator()
    {
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Choose what to dispense.").Must(l => l.Count <= 100);
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.QueueLineId).NotEmpty();
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
            line.RuleFor(l => l.SubstitutionReason).SafeText(300);
        });
        RuleFor(x => x.CounsellingNotes).SafeText(2000);
        RuleFor(x => x.DiscountPercent).InclusiveBetween(0, 100);
        RuleFor(x => x.Payments).Must(p => p.Count <= 6).WithMessage("Split the bill into at most 6 payments.");
        RuleForEach(x => x.Payments).SetValidator(new SalePaymentValidator());
    }
}

public sealed class AuthorizeRefillsRequestValidator : AbstractValidator<AuthorizeRefillsRequest>
{
    public AuthorizeRefillsRequestValidator()
    {
        RuleFor(x => x.Refills).InclusiveBetween(0, 12);
        RuleFor(x => x.Note).NotEmpty().SafeText(300).WithMessage("Record who authorised the refills (e.g. \"Dr. Mehta, phone, 25-Sep\").");
    }
}
