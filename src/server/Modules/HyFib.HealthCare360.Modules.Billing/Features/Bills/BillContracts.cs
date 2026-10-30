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
    bool IsReturn);

public sealed record ReceiptSummary(Guid Id, string ReceiptNumber, ReceiptPurpose Purpose, decimal Amount, string Modes, DateTimeOffset ReceivedAt);

public sealed record RefundSummary(Guid Id, string RefundNumber, decimal Amount, RefundStatus Status, PaymentMode? Mode, DateTimeOffset RequestedAt, DateTimeOffset? PaidAt);

public sealed record CreditNoteDto(Guid Id, string Number, CreditNoteKind Kind, decimal Amount, string Reason, DateTimeOffset IssuedAt, string? IssuedByName);

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
    decimal FreeTreatmentAmount = 0m);

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

public sealed record IssueCreditNoteRequest(CreditNoteKind Kind, decimal Amount, string Reason);

public sealed record DiscountLimitDto(Guid Id, Guid UserId, string? UserName, decimal MaxPercent, decimal? MaxAmount, string? Notes, uint Version);

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
