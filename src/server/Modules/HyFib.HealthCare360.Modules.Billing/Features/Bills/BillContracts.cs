using HyFib.HealthCare360.Modules.Patients.Features.Downtime;
using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Bills;

public sealed record BillListItem(
    Guid Id,
    string? BillNumber,
    BillType Type,
    bool IsFinalBill,
    BillStatus Status,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? VisitId,
    Guid? AdmissionId,
    PayerType PayerType,
    Guid? PayerId,
    string? PayerName,
    decimal NetAmount,
    decimal PatientAmount,
    decimal PayerAmount,
    decimal PaidAmount,
    decimal DueAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinalizedAt);

public sealed record BillLineDto(
    Guid Id,
    Guid ChargeId,
    Guid ServiceItemId,
    string ServiceCode,
    string Description,
    ServiceCategory Category,
    string? SacCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal GrossAmount,
    decimal PackageCoveredAmount,
    decimal TariffDiscountAmount,
    decimal LineDiscountAmount,
    decimal BillDiscountShare,
    decimal TaxRate,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal IgstAmount,
    decimal NetAmount,
    bool IsDiscountAllowed,
    bool IsPayerPayable,
    Guid? PractitionerId,
    string? PractitionerName,
    Guid? DepartmentId,
    DateTimeOffset ServiceDate,
    bool IsReturn,
    DeviceDetails? Device = null);

/// <param name="ReversedAmount">What reversals took back as money not received.</param>
public sealed record ReceiptSummary(Guid Id, string ReceiptNumber, ReceiptPurpose Purpose, decimal Amount, string Modes, DateTimeOffset ReceivedAt, decimal ReversedAmount = 0m);

public sealed record RefundSummary(Guid Id, string RefundNumber, decimal Amount, RefundStatus Status, PaymentMode? Mode, DateTimeOffset RequestedAt, DateTimeOffset? PaidAt);

/// <param name="RequestedByName">Who asked for it; <paramref name="IssuedByName"/> approved and issued it.</param>
/// <param name="Lines">The bill lines a credit note corrects, each with its taxable value and GST; none for a write-off.</param>
/// <param name="GstTimeBarred">Issued after the bill's GST could be reduced (CGST Act s.34(2)): it lowers the amount, not the GST.</param>
/// <param name="EInvoice">The IRN the portal gave the credit note (a <c>CRN</c>), when it has one.</param>
public sealed record CreditNoteDto(
    Guid Id,
    string Number,
    CreditNoteKind Kind,
    decimal Amount,
    string Reason,
    DateTimeOffset IssuedAt,
    string? IssuedByName,
    string? RequestedByName,
    decimal TaxableAmount = 0m,
    decimal GstAmount = 0m,
    bool GstTimeBarred = false,
    IReadOnlyList<CreditNoteLineDto>? Lines = null,
    EInvoiceStamp? EInvoice = null);

public sealed record CreditNoteLineDto(
    Guid BillLineId, string Description, string? SacCode, decimal GstRate, decimal Amount, decimal TaxableAmount, decimal Cgst, decimal Sgst, decimal Igst);

public sealed record DiscountApprovalDto(
    Guid Id,
    Guid BillId,
    string? BillNumber,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    decimal RequestedAmount,
    decimal DiscountBase,
    decimal? BillDiscountPercent,
    decimal? BillDiscountAmount,
    string Reason,
    DiscountApprovalStatus Status,
    string? RequestedByName,
    DateTimeOffset RequestedAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? Remarks);

/// <summary>Everything the bill screen and the GST invoice/receipt print need.</summary>
public sealed record BillDetail(
    Guid Id,
    string? BillNumber,
    BillType Type,
    bool IsFinalBill,
    BillStatus Status,
    Guid FacilityId,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid? IpAccountId,
    PayerType PayerType,
    Guid? PayerId,
    string? PayerName,
    Guid? TariffPlanId,
    string? TariffPlanName,
    Guid? PolicyId,
    Guid? PreAuthorizationId,
    decimal CoPayPercent,
    decimal? PayerCoverageLimit,
    decimal? BillDiscountPercent,
    decimal? BillDiscountValue,
    string? DiscountReason,
    decimal ApprovedDiscountAmount,
    decimal GrossAmount,
    decimal PackageCoveredAmount,
    decimal TariffDiscountAmount,
    decimal LineDiscountAmount,
    decimal BillDiscountAmount,
    decimal DiscountAmount,
    decimal TaxableAmount,
    decimal CgstAmount,
    decimal SgstAmount,
    decimal IgstAmount,
    string? PlaceOfSupply,
    bool IsInterState,
    decimal TaxAmount,
    decimal RoundOffAmount,
    decimal NetAmount,
    decimal PayerAmount,
    decimal PatientAmount,
    decimal CoPayAmount,
    decimal NonPayableAmount,
    decimal PaidAmount,
    decimal RefundedAmount,
    decimal CreditAmount,
    decimal DueAmount,
    decimal RefundableAmount,
    decimal PayerSettledAmount,
    decimal PayerDisallowedAmount,
    bool IsTaxInvoice,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinalizedAt,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    string? Notes,
    IReadOnlyList<BillLineDto> Lines,
    IReadOnlyList<ReceiptSummary> Receipts,
    IReadOnlyList<RefundSummary> Refunds,
    IReadOnlyList<CreditNoteDto> CreditNotes,
    DiscountApprovalDto? PendingDiscount,
    BillPackageSummary? Package,
    uint Version,
    EInvoiceStamp? EInvoice = null,
    string? FreeTreatmentReason = null,
    decimal FreeTreatmentAmount = 0m,
    IReadOnlyList<DebitNoteDto>? DebitNotes = null,
    decimal DebitAmount = 0m,
    decimal PayerDebitAmount = 0m,
    string? GstFilingWarning = null);

/// <summary>
/// The package a bill is priced at: an OP bill's own, or the IP account's on a final bill. <see cref="Price"/> is the
/// package line as billed, <see cref="Actuals"/> the other services at their full amount, of which the package
/// <see cref="Covered"/> part and the patient (or payer) is billed <see cref="BeyondPackage"/>.
/// </summary>
public sealed record BillPackageSummary(Guid PackageId, string Code, string Name, decimal Price, decimal Actuals, decimal Covered, decimal BeyondPackage);

/// <summary>Bills an OP draft at a package, or no longer (<c>null</c>).</summary>
public sealed record SetBillPackageRequest(Guid? PackageId, uint Version);

public sealed class SetBillPackageRequestValidator : AbstractValidator<SetBillPackageRequest>
{
    public SetBillPackageRequestValidator()
    {
        RuleFor(x => x.PackageId).OptionalId();
        RuleFor(x => x.Version).SeenVersion();
    }
}

/// <summary>Draft an OP bill from a visit's unbilled charges (or a patient's charges without a visit).</summary>
public sealed record CreateBillRequest(Guid? VisitId, Guid? PatientId, IReadOnlyList<Guid>? ChargeIds);

public sealed record AddBillLineRequest(
    Guid ServiceItemId,
    decimal Quantity,
    decimal? UnitPrice,
    string? Description,
    Guid? PractitionerId,
    Guid? DepartmentId,
    uint Version);

public sealed record BillVersionRequest(uint Version);

/// <summary>Refreshing or finalizing a bill says which version of it was on screen.</summary>
public sealed class BillVersionRequestValidator : AbstractValidator<BillVersionRequest>
{
    public BillVersionRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

/// <param name="Paper">
/// A bill written on paper during a downtime: the downtime bill number on it and the time on it, which the bill keeps
/// (docs/modules/downtime.md). Null for a bill finalized now, which takes the next number.
/// </param>
public sealed record FinalizeBillRequest(uint Version, PaperNumber? Paper = null);

public sealed class FinalizeBillRequestValidator : AbstractValidator<FinalizeBillRequest>
{
    public FinalizeBillRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Version).SeenVersion();
        RuleFor(x => x.Paper!).SetValidator(new PaperNumberValidator(time)).OverridePropertyName("paper").When(x => x.Paper is not null);
    }
}

public sealed record LineDiscountInput(Guid LineId, decimal? Percent, decimal? Amount);

/// <summary>Replaces all staff discounts on a draft bill; above the user's limit the request waits for approval.</summary>
public sealed record ApplyDiscountRequest(
    decimal? BillDiscountPercent,
    decimal? BillDiscountAmount,
    IReadOnlyList<LineDiscountInput>? LineDiscounts,
    string Reason,
    uint Version);

public sealed record DiscountOutcome(bool Applied, Guid? ApprovalId, decimal RequestedDiscount, decimal AllowedWithoutApproval);

public sealed record CancelBillRequest(string Reason, uint Version);

public sealed record DecideDiscountRequest(string? Remarks);

/// <summary>A credit note or write-off asked for on a finalized bill; it is issued when an approver approves it.</summary>
/// <param name="Lines">
/// The bill lines a credit note corrects and what it takes off each (GST included); <paramref name="Amount"/> is their sum.
/// Required when the bill carries GST, since the GST credited is each line's own; not for a write-off.
/// </param>
public sealed record IssueCreditNoteRequest(CreditNoteKind Kind, decimal Amount, string Reason, IReadOnlyList<CreditNoteLineRequest>? Lines = null);

public sealed record CreditNoteLineRequest(Guid BillLineId, decimal Amount);

/// <summary>
/// A debit note: the services charged short on a finalized bill, each with the taxable value it is raised by (GST at the
/// line's rate is added), charged to the patient or the payer.
/// </summary>
public sealed record IssueDebitNoteRequest(DebitNoteParty Party, string Reason, IReadOnlyList<DebitNoteLineRequest> Lines);

public sealed record DebitNoteLineRequest(Guid BillLineId, decimal TaxableAmount);

public sealed record DebitNoteDto(
    Guid Id,
    string Number,
    DebitNoteParty Party,
    decimal Amount,
    decimal TaxableAmount,
    decimal GstAmount,
    string Reason,
    DateTimeOffset IssuedAt,
    string? IssuedByName,
    string? RequestedByName,
    IReadOnlyList<DebitNoteLineDto> Lines,
    EInvoiceStamp? EInvoice = null);

public sealed record DebitNoteLineDto(
    Guid BillLineId, string Description, string? SacCode, decimal GstRate, decimal Amount, decimal TaxableAmount, decimal Cgst, decimal Sgst, decimal Igst);

public sealed record DiscountLimitDto(Guid Id, Guid UserId, string? UserName, decimal MaxPercent, decimal? MaxAmount, string? Notes, uint Version);

/// <summary>A user's discount limit raised (or removed, so a higher default applies) that waits for a second approver.</summary>
/// <param name="AskedByYou">The reader asked for it, so they may withdraw it and may not approve it.</param>
public sealed record PendingDiscountLimitDto(
    Guid ApprovalId, Guid UserId, string? UserName, string Before, string Asked, string RequestedBy, DateTimeOffset RequestedAt, bool AskedByYou);

public sealed record SaveDiscountLimitRequest(Guid UserId, decimal MaxPercent, decimal? MaxAmount, string? Notes, uint? Version = null);

public sealed class CreateBillRequestValidator : AbstractValidator<CreateBillRequest>
{
    public CreateBillRequestValidator()
    {
        RuleFor(x => x).Must(x => x.VisitId is not null || x.PatientId is not null)
            .WithName("visitId").WithMessage("Choose the visit (or the patient) to bill.");
        RuleFor(x => x.ChargeIds).Must(ids => ids is null || ids.Count <= 500).WithMessage("Bill at most 500 services at a time.");
    }
}

public sealed class AddBillLineRequestValidator : AbstractValidator<AddBillLineRequest>
{
    public AddBillLineRequestValidator()
    {
        RuleFor(x => x.ServiceItemId).NotEmpty();
        RuleFor(x => x.Quantity).Quantity();
        RuleFor(x => x.UnitPrice).OptionalAmount();
        RuleFor(x => x.Description).SafeText(300);
    }
}

public sealed class ApplyDiscountRequestValidator : AbstractValidator<ApplyDiscountRequest>
{
    public ApplyDiscountRequestValidator()
    {
        RuleFor(x => x.BillDiscountPercent).OptionalPercent();
        RuleFor(x => x.BillDiscountAmount).OptionalAmount();
        RuleFor(x => x).Must(x => x.BillDiscountPercent is null || x.BillDiscountAmount is null)
            .WithName("billDiscountAmount").WithMessage("Give the bill discount as a percentage or an amount, not both.");
        RuleForEach(x => x.LineDiscounts).ChildRules(line =>
        {
            line.RuleFor(l => l.LineId).NotEmpty();
            line.RuleFor(l => l.Percent).OptionalPercent();
            line.RuleFor(l => l.Amount).OptionalAmount();
            line.RuleFor(l => l).Must(l => l.Percent is null || l.Amount is null).WithName("amount").WithMessage("Give a percentage or an amount, not both.");
        });
        RuleFor(x => x.Reason).Reason();
    }
}

public sealed class CancelBillRequestValidator : AbstractValidator<CancelBillRequest>
{
    public CancelBillRequestValidator() => RuleFor(x => x.Reason).Reason();
}

public sealed class DecideDiscountRequestValidator : AbstractValidator<DecideDiscountRequest>
{
    public DecideDiscountRequestValidator() => RuleFor(x => x.Remarks).SafeText(300);
}

public sealed class IssueCreditNoteRequestValidator : AbstractValidator<IssueCreditNoteRequest>
{
    public IssueCreditNoteRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Amount).Amount();
        RuleFor(x => x.Reason).Reason();
        RuleFor(x => x.Lines).Must(lines => lines is null || lines.Count == 0)
            .When(x => x.Kind == CreditNoteKind.WriteOff)
            .WithMessage("A write-off is of the balance due, not of services: leave out the lines.");
        RuleFor(x => x.Lines).Must(lines => lines is null || lines.Count <= 200).WithMessage("Credit at most 200 services on one note.");
        RuleFor(x => x.Lines).Must(lines => lines is null || lines.Select(l => l.BillLineId).Distinct().Count() == lines.Count)
            .WithMessage("Name each service once.");
        RuleFor(x => x).Must(x => x.Lines is not { Count: > 0 } lines || Money.Round(lines.Sum(l => l.Amount)) == Money.Round(x.Amount))
            .WithName("amount").WithMessage("The amount is the sum of the services' credits.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.BillLineId).NotEmpty().WithMessage("Choose the service.");
            line.RuleFor(l => l.Amount).Amount();
        });
    }
}

public sealed class IssueDebitNoteRequestValidator : AbstractValidator<IssueDebitNoteRequest>
{
    public IssueDebitNoteRequestValidator()
    {
        RuleFor(x => x.Party).IsInEnum();
        RuleFor(x => x.Reason).Reason();
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Name the services charged short, with the taxable value each is raised by.");
        RuleFor(x => x.Lines).Must(lines => lines is null || lines.Count <= 200).WithMessage("Raise at most 200 services on one note.");
        RuleFor(x => x.Lines).Must(lines => lines is null || lines.Select(l => l.BillLineId).Distinct().Count() == lines.Count)
            .WithMessage("Name each service once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.BillLineId).NotEmpty().WithMessage("Choose the service.");
            line.RuleFor(l => l.TaxableAmount).Amount();
        });
    }
}

public sealed class SaveDiscountLimitRequestValidator : AbstractValidator<SaveDiscountLimitRequest>
{
    public SaveDiscountLimitRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.MaxPercent).Percent();
        RuleFor(x => x.MaxAmount).OptionalAmount();
        RuleFor(x => x.Notes).SafeText(300);
    }
}
