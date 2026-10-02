using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum RefundStatus
{
    Requested = 0,
    Approved,
    Rejected,
    Paid,
}

public enum RefundPurpose
{
    /// <summary>Overpayment, credit note or cancelled bill.</summary>
    Bill = 0,

    /// <summary>Unused in-patient advance (IPB-08).</summary>
    Deposit,

    /// <summary>Unused patient advance, for example the prepayment of an appointment that was cancelled.</summary>
    Advance,
}

/// <summary>
/// Money returned to a patient (MB-05). Requested by the counter, approved by a second person (segregation of duties),
/// then paid out in a stated mode — so no single user can move cash out of the hospital.
/// </summary>
public sealed class Refund : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string RefundNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public RefundPurpose Purpose { get; set; }
    public Guid? BillId { get; set; }
    public Guid? IpAccountId { get; set; }

    /// <summary>Patient advance refunded (purpose Advance) or restored (an adjustment returned when its bill was cancelled).</summary>
    public Guid? AdvanceId { get; set; }

    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public RefundStatus Status { get; set; }
    public Guid? RequestedBy { get; set; }
    public string? RequestedByName { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionRemarks { get; set; }
    public PaymentMode? Mode { get; set; }
    public string? Reference { get; set; }
    public Guid? PaidBy { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public Guid? ShiftId { get; set; }
    public string? Counter { get; set; }
    public string? GatewayRefundId { get; set; }

    /// <summary>
    /// For a refund through the payment gateway: whether the bank has returned the money yet. The refund is paid out in
    /// HC360 when the gateway accepts it; this follows it until the gateway reports it processed or failed.
    /// </summary>
    public PaymentRefundStatus? GatewayStatus { get; set; }

    /// <summary>The bank's reference for the gateway refund (ARN, RRN or UTR), which the patient's bank can trace.</summary>
    public string? GatewayReference { get; set; }

    /// <summary>When HC360 learnt that the gateway refund was processed or failed.</summary>
    public DateTimeOffset? GatewaySettledAt { get; set; }

    public string? GatewayError { get; set; }

    /// <summary>Still owed to the patient (counts against the refundable balance).</summary>
    public bool IsOutstanding => Status is RefundStatus.Requested or RefundStatus.Approved;

    /// <summary>Paid through the gateway and not yet confirmed (or failed and not yet retried).</summary>
    public bool IsGatewayRefundOpen => GatewayRefundId is not null && GatewayStatus is null or PaymentRefundStatus.Pending;

    /// <summary>The gateway accepted the refund (first try or a retry after a failure): it is pending until the bank confirms it.</summary>
    public void StartGatewayRefund(string gatewayRefundId)
    {
        GatewayRefundId = gatewayRefundId;
        GatewayStatus = PaymentRefundStatus.Pending;
        GatewayReference = null;
        GatewaySettledAt = null;
        GatewayError = null;
    }

    /// <summary>
    /// Applies what the gateway reports about the refund. A processed or failed refund is final; an unanswered question
    /// (an error with no status) changes nothing. Returns true when something changed.
    /// </summary>
    public bool ApplyGatewayStatus(RefundStatusResult result, DateTimeOffset now)
    {
        if (GatewayRefundId is null || result.RefundId != GatewayRefundId || GatewayStatus is PaymentRefundStatus.Processed or PaymentRefundStatus.Failed)
        {
            return false;
        }

        var reference = result.Reference is { Length: > 0 and <= 64 } r ? r : GatewayReference;
        if (result.Status == PaymentRefundStatus.Pending)
        {
            var changed = GatewayStatus != PaymentRefundStatus.Pending || reference != GatewayReference;
            if (result.Error is null && changed)
            {
                GatewayStatus = PaymentRefundStatus.Pending;
                GatewayReference = reference;
                return true;
            }

            return false;
        }

        GatewayStatus = result.Status;
        GatewayReference = reference;
        GatewaySettledAt = now;
        var error = Clean(result.Error) ?? "The gateway could not return the money.";
        GatewayError = result.Status == PaymentRefundStatus.Failed ? error[..Math.Min(error.Length, 300)] : null;
        return true;
    }

    public static Refund Request(
        Guid facilityId, string number, RefundPurpose purpose, Guid patientId, Guid? billId, Guid? ipAccountId, decimal amount, string reason,
        Guid? requestedBy, string? requestedByName, DateTimeOffset now) => new()
        {
            FacilityId = facilityId,
            RefundNumber = number,
            Purpose = purpose,
            PatientId = patientId,
            BillId = billId,
            IpAccountId = ipAccountId,
            Amount = Money.Round(amount),
            Reason = reason.Trim(),
            Status = RefundStatus.Requested,
            RequestedBy = requestedBy,
            RequestedByName = requestedByName,
            RequestedAt = now,
        };

    /// <summary>
    /// Returns an advance adjustment to the deposit when an IP bill is cancelled. No money leaves the hospital, so it is
    /// complete immediately and needs no approval.
    /// </summary>
    public static Refund ReturnToAdvance(Guid facilityId, string number, Guid patientId, Guid billId, Guid? ipAccountId, decimal amount, string reason, Guid? userId, DateTimeOffset now)
    {
        var refund = Request(facilityId, number, RefundPurpose.Bill, patientId, billId, ipAccountId, amount, reason, userId, null, now);
        refund.Status = RefundStatus.Paid;
        refund.Mode = PaymentMode.Advance;
        refund.DecidedBy = userId;
        refund.DecidedAt = now;
        refund.PaidBy = userId;
        refund.PaidAt = now;
        return refund;
    }

    public Result Approve(Guid approverId, string? remarks, DateTimeOffset now)
    {
        var check = CheckDecision(approverId);
        if (check.IsFailure)
        {
            return check;
        }

        Status = RefundStatus.Approved;
        DecidedBy = approverId;
        DecidedAt = now;
        DecisionRemarks = Clean(remarks);
        return Result.Success();
    }

    public Result Reject(Guid approverId, string reason, DateTimeOffset now)
    {
        var check = CheckDecision(approverId);
        if (check.IsFailure)
        {
            return check;
        }

        Status = RefundStatus.Rejected;
        DecidedBy = approverId;
        DecidedAt = now;
        DecisionRemarks = Clean(reason);
        return Result.Success();
    }

    public Result MarkPaid(PaymentMode mode, string? reference, Guid? userId, Guid? shiftId, string? counter, DateTimeOffset now)
    {
        if (Status != RefundStatus.Approved)
        {
            return Error.Rule("billing.refund_not_approved", "Only approved refunds can be paid out.");
        }

        if (mode is PaymentMode.Advance or PaymentMode.Insurance)
        {
            return Error.Field("mode", "Choose how the money is returned: cash, card reversal, UPI, bank transfer, cheque or the payment gateway.");
        }

        if (mode != PaymentMode.Cash && string.IsNullOrWhiteSpace(reference))
        {
            return Error.Field("reference", "Enter the transfer, reversal or cheque reference.");
        }

        Status = RefundStatus.Paid;
        Mode = mode;
        Reference = Clean(reference);
        PaidBy = userId;
        PaidAt = now;
        ShiftId = shiftId;
        Counter = counter;
        Raise(new RefundPaid(Id, RefundNumber, FacilityId, PatientId, BillId, Amount, now));
        return Result.Success();
    }

    private Result CheckDecision(Guid approverId)
    {
        if (Status != RefundStatus.Requested)
        {
            return Error.Rule("billing.refund_not_pending", "This refund is not awaiting approval.");
        }

        return RequestedBy == approverId
            ? Error.Forbidden("billing.refund_self_approval", "A refund must be approved by someone other than the person who requested it.")
            : Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum CreditNoteKind
{
    /// <summary>Commercial adjustment after billing (wrong rate, service not rendered).</summary>
    CreditNote = 0,

    /// <summary>Unrecoverable due written off (bad debt), with approval.</summary>
    WriteOff,
}

/// <summary>Reduces what the patient owes on a finalized bill (MB-05); issued only by approvers and always audited.</summary>
public sealed class CreditNote : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public CreditNoteKind Kind { get; set; }
    public Guid BillId { get; set; }
    public Guid PatientId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid? IssuedBy { get; set; }
    public string? IssuedByName { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
}
