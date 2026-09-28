namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum IpAccountStatus
{
    Open = 0,
    DischargeInitiated,
    Discharged,

    /// <summary>Discharged and fully settled.</summary>
    Closed,

    /// <summary>The admission was cancelled (made in error); any advance still held must be refunded.</summary>
    Cancelled,
}

/// <summary>
/// The in-patient billing account (IPB-01..08), opened automatically when a patient is admitted. It fixes the tariff
/// and payer for the stay, tracks the advance (deposit) balance and the current bed class used for room-category
/// pricing, and carries the package and any management approval to discharge with dues.
/// </summary>
public sealed class IpAccount : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public string AdmissionNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid VisitId { get; set; }
    public PayerType PayerType { get; set; }
    public Guid? PayerId { get; set; }
    public Guid? TariffPlanId { get; set; }
    public Guid? PolicyId { get; set; }
    public Guid? AttendingPractitionerId { get; set; }
    public Guid? WardId { get; set; }
    public Guid? BedId { get; set; }

    /// <summary>Service item of the current bed (BED-GEN, BED-PVT…): selects the tariff's bed-class multiplier.</summary>
    public Guid? BedServiceItemId { get; set; }

    public DateTimeOffset AdmittedAt { get; set; }
    public DateTimeOffset? DischargeInitiatedAt { get; set; }
    public DateTimeOffset? DischargedAt { get; set; }
    public string? DischargeType { get; set; }
    public IpAccountStatus Status { get; set; }

    /// <summary>Cost estimate given to the patient/insurer at admission.</summary>
    public decimal EstimatedAmount { get; set; }

    public decimal DepositReceived { get; set; }
    public decimal DepositAdjusted { get; set; }
    public decimal DepositRefunded { get; set; }
    public Guid? PackageId { get; set; }
    public DateTimeOffset? PackageAssignedAt { get; set; }
    public bool DischargeWithDuesApproved { get; set; }
    public Guid? DischargeApprovedBy { get; set; }
    public string? DischargeApprovalReason { get; set; }
    public DateTimeOffset? DischargeApprovedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public string? Notes { get; set; }

    public decimal DepositBalance => DepositReceived - DepositAdjusted - DepositRefunded;

    public bool IsActive => Status is IpAccountStatus.Open or IpAccountStatus.DischargeInitiated;

    public static IpAccount Open(PatientAdmitted admitted, Guid? payerId, Guid? tariffPlanId, Guid? policyId) => new()
    {
        FacilityId = admitted.FacilityId,
        AdmissionId = admitted.AdmissionId,
        AdmissionNumber = admitted.AdmissionNumber,
        PatientId = admitted.PatientId,
        VisitId = admitted.VisitId,
        PayerType = admitted.PayerType,
        PayerId = payerId,
        TariffPlanId = tariffPlanId,
        PolicyId = policyId,
        AttendingPractitionerId = admitted.AdmittingPractitionerId,
        WardId = admitted.WardId,
        BedId = admitted.BedId,
        BedServiceItemId = admitted.BedServiceItemId,
        AdmittedAt = admitted.AdmittedAt,
        Status = IpAccountStatus.Open,
    };

    public Result RecordDeposit(decimal amount)
    {
        if (amount <= 0)
        {
            return Error.Rule("billing.amount_invalid", "Enter an amount greater than zero.");
        }

        if (Status is IpAccountStatus.Closed or IpAccountStatus.Cancelled)
        {
            return Error.Rule("billing.ip_account_closed", "This in-patient account is closed.");
        }

        DepositReceived += amount;
        return Result.Success();
    }

    /// <summary>
    /// The admission was cancelled. The account stops accepting advances and packages; services other modules already
    /// charged stay billable (a final bill can still be prepared) and any advance is refunded through the normal flow.
    /// </summary>
    public void CancelAdmission(string reason, DateTimeOffset at)
    {
        if (Status is IpAccountStatus.Closed or IpAccountStatus.Cancelled)
        {
            return;
        }

        Status = IpAccountStatus.Cancelled;
        CancelledAt = at;
        CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }

    public Result AdjustDeposit(decimal amount) => Consume(amount, adjust: true);

    public Result RefundDeposit(decimal amount) => Consume(amount, adjust: false);

    /// <summary>Puts an adjustment back into the advance (the bill it paid was cancelled).</summary>
    public void ReverseAdjustment(decimal amount) => DepositAdjusted = Math.Max(DepositAdjusted - Math.Max(amount, 0m), 0m);

    public void MoveToBed(Guid? bedId, Guid? bedServiceItemId)
    {
        BedId = bedId ?? BedId;
        BedServiceItemId = bedServiceItemId ?? BedServiceItemId;
    }

    public void MarkDischargeInitiated(DateTimeOffset at)
    {
        if (Status == IpAccountStatus.Open)
        {
            Status = IpAccountStatus.DischargeInitiated;
        }

        DischargeInitiatedAt ??= at;
    }

    public void MarkDischarged(DateTimeOffset at, string? dischargeType)
    {
        if (Status is not (IpAccountStatus.Closed or IpAccountStatus.Cancelled))
        {
            Status = IpAccountStatus.Discharged;
        }

        DischargedAt = at;
        DischargeType = dischargeType;
    }

    public void Close()
    {
        if (Status == IpAccountStatus.Discharged)
        {
            Status = IpAccountStatus.Closed;
        }
    }

    public Result AssignPackage(Guid? packageId, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Error.Rule("billing.ip_account_inactive", "Packages can be changed only while the patient is admitted.");
        }

        PackageId = packageId;
        PackageAssignedAt = packageId is null ? null : now;
        return Result.Success();
    }

    /// <summary>Management approval to discharge although dues remain (recorded against the approver).</summary>
    public Result ApproveDischargeWithDues(Guid approverId, string reason, DateTimeOffset now)
    {
        if (Status == IpAccountStatus.Closed)
        {
            return Error.Rule("billing.ip_account_closed", "This in-patient account is already closed.");
        }

        DischargeWithDuesApproved = true;
        DischargeApprovedBy = approverId;
        DischargeApprovalReason = reason.Trim();
        DischargeApprovedAt = now;
        return Result.Success();
    }

    private Result Consume(decimal amount, bool adjust)
    {
        if (amount <= 0)
        {
            return Error.Rule("billing.amount_invalid", "Enter an amount greater than zero.");
        }

        if (amount > DepositBalance)
        {
            return Error.Rule("billing.deposit_insufficient", $"The advance balance is ₹{Money.FormatInr(Math.Max(DepositBalance, 0m))}.");
        }

        if (adjust)
        {
            DepositAdjusted += amount;
        }
        else
        {
            DepositRefunded += amount;
        }

        return Result.Success();
    }
}

/// <summary>Billing position of one admission, as needed for discharge clearance.</summary>
public sealed record AdmissionBillingPosition(
    int UnbilledCount,
    decimal UnbilledAmount,
    bool HasAnyCharges,
    bool HasFinalBill,
    decimal PatientDue,
    decimal DepositBalance,
    bool DischargeWithDuesApproved);

/// <summary>
/// Discharge billing clearance (IPB-07): every IP charge is billed, the final bill exists and the patient's share is
/// settled (the payer share is covered by the pre-authorisation, see the bill split) — or management approved a
/// discharge with dues. The outstanding amount tells the ward how much is still to be collected.
/// </summary>
public static class DischargeClearanceRules
{
    public static DischargeClearance Evaluate(AdmissionBillingPosition position)
    {
        var outstanding = Money.Round(position.UnbilledAmount + position.PatientDue);
        if (position.DischargeWithDuesApproved)
        {
            return new DischargeClearance(true, outstanding, outstanding > 0 ? "Discharge with dues approved by management." : null);
        }

        if (!position.HasAnyCharges)
        {
            return new DischargeClearance(true, 0m, null);
        }

        if (position.UnbilledCount > 0)
        {
            return new DischargeClearance(false, outstanding,
                $"{position.UnbilledCount} service(s) worth ₹{Money.FormatInr(position.UnbilledAmount)} are not on a finalized bill yet.");
        }

        if (!position.HasFinalBill)
        {
            return new DischargeClearance(false, outstanding, "The final bill has not been prepared.");
        }

        if (position.PatientDue > 0)
        {
            var advance = position.DepositBalance > 0 ? $" An advance of ₹{Money.FormatInr(position.DepositBalance)} can be adjusted." : string.Empty;
            return new DischargeClearance(false, outstanding, $"₹{Money.FormatInr(position.PatientDue)} is due from the patient.{advance}");
        }

        return new DischargeClearance(true, 0m, null);
    }
}
