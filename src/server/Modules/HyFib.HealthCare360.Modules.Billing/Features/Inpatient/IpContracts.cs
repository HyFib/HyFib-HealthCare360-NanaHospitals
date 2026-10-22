using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Inpatient;

public sealed record IpAccountListItem(
    Guid Id,
    Guid AdmissionId,
    string AdmissionNumber,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    PayerType PayerType,
    string? PayerName,
    IpAccountStatus Status,
    DateTimeOffset AdmittedAt,
    DateTimeOffset? DischargedAt,
    decimal EstimatedAmount,
    decimal DepositBalance,
    bool HasPackage);

public sealed record IpAccountDetail(
    Guid Id,
    Guid AdmissionId,
    string AdmissionNumber,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid VisitId,
    PayerType PayerType,
    Guid? PayerId,
    string? PayerName,
    Guid? TariffPlanId,
    string? TariffPlanName,
    Guid? PolicyId,
    Guid? AttendingPractitionerId,
    string? AttendingPractitionerName,
    Guid? BedServiceItemId,
    string? BedServiceCode,
    DateTimeOffset AdmittedAt,
    DateTimeOffset? DischargeInitiatedAt,
    DateTimeOffset? DischargedAt,
    IpAccountStatus Status,
    decimal EstimatedAmount,
    decimal DepositReceived,
    decimal DepositAdjusted,
    decimal DepositRefunded,
    decimal DepositBalance,
    Guid? PackageId,
    string? PackageName,
    bool DischargeWithDuesApproved,
    string? DischargeApprovalReason,
    string? Notes,
    uint Version);

/// <summary>
/// Changes the payer/tariff of an admission (for example cash converted to cashless mid-stay). With
/// <paramref name="RepriceUnbilledCharges"/> the unbilled services are re-priced on the new tariff.
/// </summary>
public sealed record UpdateIpAccountRequest(
    decimal EstimatedAmount,
    PayerType PayerType,
    Guid? PayerId,
    Guid? TariffPlanId,
    Guid? PolicyId,
    bool RepriceUnbilledCharges,
    string? Notes,
    uint Version);

public sealed record AssignPackageRequest(Guid? PackageId, uint Version);

public sealed record RunningBillCategory(ServiceCategory Category, int Items, decimal Gross, decimal Discount, decimal Tax, decimal Net);

public sealed record RunningBillDay(DateOnly Date, int Items, decimal Net);

public sealed record RunningBillLine(
    Guid ChargeId,
    DateTimeOffset ServiceDate,
    string ServiceCode,
    string Description,
    ServiceCategory Category,
    decimal Quantity,
    decimal UnitPrice,
    decimal NetAmount,
    ChargeStatus Status,
    Guid? BillId,
    string? PractitionerName,
    bool IsReturn);

public sealed record PackageLineStatus(Guid ChargeId, string ServiceCode, string Description, decimal Gross, decimal Covered, decimal Excess);

/// <summary>Package vs actuals: what the package absorbs, the excess billed separately and the hospital's gain/loss.</summary>
public sealed record PackageStatus(
    Guid PackageId,
    string Code,
    string Name,
    decimal PackagePrice,
    decimal Actuals,
    decimal Covered,
    decimal Excess,
    decimal Variance,
    IReadOnlyList<PackageLineStatus> Lines);

public sealed record InsuranceEstimate(Guid? PayerId, string? PayerName, decimal? CoverageLimit, decimal CoPayPercent, decimal PayerShare, decimal PatientShare);

/// <summary>The IP running bill (IPB-02): every service so far by category and by day, with deposits and estimates.</summary>
public sealed record RunningBill(
    Guid IpAccountId,
    string AdmissionNumber,
    IpAccountStatus Status,
    IReadOnlyList<RunningBillCategory> ByCategory,
    IReadOnlyList<RunningBillDay> ByDate,
    IReadOnlyList<RunningBillLine> Lines,
    decimal GrossTotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal NetTotal,
    decimal UnbilledNet,
    decimal BilledNet,
    decimal EstimatedPatientPayable,
    decimal PaidOnBills,
    decimal DepositBalance,
    decimal EstimatedBalance,
    decimal EstimatedAmount,
    PackageStatus? Package,
    InsuranceEstimate? Insurance,
    decimal ReturnsNet);

public sealed record CreateInterimBillRequest(DateOnly? UpTo);

/// <summary>
/// A final settlement: the final bill it followed (none for a stay whose interim bills took every service, or with no
/// services), the advance adjusted against the open bills, what is still due, and the refund requested of what is left.
/// </summary>
public sealed record SettlementResult(Guid? FinalBillId, decimal AdvanceAdjusted, decimal RemainingDue, Guid? RefundId, decimal RefundRequested);

public sealed record ApproveDischargeWithDuesRequest(string Reason);

public sealed record ClearanceDto(
    bool Cleared,
    decimal Outstanding,
    string? Reason,
    int UnbilledCount,
    decimal UnbilledAmount,
    bool HasFinalBill,
    decimal PatientDue,
    decimal DepositBalance,
    bool DischargeWithDuesApproved,
    bool BilledOnInterimBills);

public sealed class UpdateIpAccountRequestValidator : AbstractValidator<UpdateIpAccountRequest>
{
    public UpdateIpAccountRequestValidator()
    {
        RuleFor(x => x.EstimatedAmount).NonNegativeAmount();
        RuleFor(x => x.PayerType).IsInEnum();
        RuleFor(x => x.PayerId).NotNull()
            .When(x => x.PayerType is PayerType.Insurance or PayerType.Corporate or PayerType.GovernmentScheme)
            .WithMessage("Choose the insurer, corporate or scheme.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class ApproveDischargeWithDuesRequestValidator : AbstractValidator<ApproveDischargeWithDuesRequest>
{
    public ApproveDischargeWithDuesRequestValidator() => RuleFor(x => x.Reason).Reason();
}
