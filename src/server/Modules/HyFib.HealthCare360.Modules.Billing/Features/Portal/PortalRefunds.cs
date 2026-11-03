using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Features.Portal;

/// <summary>Where a refund stands, in the patient's terms.</summary>
public enum PortalRefundStage
{
    /// <summary>Asked for at the counter; a second person has not approved it yet.</summary>
    Requested = 0,

    /// <summary>Approved; the money has not been paid out yet.</summary>
    Approved,

    /// <summary>Not approved. Why is the hospital's to explain: the reason given is a staff note and is not shown.</summary>
    NotApproved,

    /// <summary>Paid out at the counter: in cash, or by transfer, card reversal, UPI or cheque with its reference.</summary>
    PaidOut,

    /// <summary>Kept as an advance on the patient's account (an advance a cancelled bill had used went back to it).</summary>
    KeptAsAdvance,

    /// <summary>Sent back through the payment gateway to the card or account that paid; the bank has not confirmed it yet.</summary>
    WithTheBank,

    /// <summary>The bank has confirmed the money is back, with its reference (ARN, RRN or UTR) when the gateway gives one.</summary>
    Credited,

    /// <summary>The bank or the gateway could not return the money: it is still with the hospital, which pays it again.</summary>
    BankReturned,

    /// <summary>Recorded as paid but the money never reached the patient: a new refund pays it, and is shown on its own.</summary>
    Replaced,
}

/// <summary>
/// A refund as the patient sees it in the portal: how much, for what, where it stands and the reference their bank or the
/// counter can trace it by. Never the staff's names, the approver's remarks or the gateway's own error text.
/// </summary>
/// <param name="SentAt">When the money left the hospital: paid out at the counter, or the latest time it was sent through the gateway.</param>
/// <param name="Reference">The bank's reference for a gateway refund (ARN, RRN or UTR), or the counter's transfer, reversal or cheque reference.</param>
/// <param name="CreditedAt">When the bank confirmed a gateway refund.</param>
public sealed record PortalRefund(
    Guid Id,
    string RefundNumber,
    Guid PatientId,
    string? PatientName,
    Guid? BillId,
    string? BillNumber,
    RefundPurpose Purpose,
    decimal Amount,
    PortalRefundStage Stage,
    DateTimeOffset RequestedAt,
    DateTimeOffset? SentAt,
    PaymentMode? Mode,
    string? Reference,
    DateTimeOffset? CreditedAt);

internal static class PortalRefunds
{
    /// <summary>
    /// The stage of a refund for the patient (<see cref="PortalRefundStage"/>) at <paramref name="now"/>. A gateway refund
    /// the bank returned stays returned until it is sent again; one whose retry stopped part way (the server stopped while
    /// sending it) is returned too, by the same rule that lets the counter send it again.
    /// </summary>
    public static PortalRefundStage StageOf(Refund refund, DateTimeOffset now) => refund switch
    {
        { Status: RefundStatus.Requested } => PortalRefundStage.Requested,
        { Status: RefundStatus.Approved } => PortalRefundStage.Approved,
        { Status: RefundStatus.Rejected } => PortalRefundStage.NotApproved,
        { PayoutReversedAt: not null } => PortalRefundStage.Replaced,
        { Mode: PaymentMode.Advance } => PortalRefundStage.KeptAsAdvance,
        { GatewayRefundId: not null, GatewayStatus: PaymentRefundStatus.Processed } => PortalRefundStage.Credited,
        { GatewayRefundId: not null } when refund.CanSendGatewayRefundAgain(now) => PortalRefundStage.BankReturned,
        { GatewayRefundId: not null } => PortalRefundStage.WithTheBank,
        _ => PortalRefundStage.PaidOut,
    };

    /// <summary>The refunds of these patients, newest first, optionally only those of one bill.</summary>
    public static async Task<List<PortalRefund>> OfAsync(
        HospitalDbContext db, IReadOnlyCollection<Guid> patientIds, Guid? billId, TimeProvider time, CancellationToken ct)
    {
        var refunds = await db.Set<Refund>().AsNoTracking()
            .Where(r => patientIds.Contains(r.PatientId) && (billId == null || r.BillId == billId))
            .OrderByDescending(r => r.RequestedAt)
            .ToListAsync(ct);
        if (refunds.Count == 0)
        {
            return [];
        }

        var billIds = refunds.Where(r => r.BillId is not null).Select(r => r.BillId!.Value).Distinct().ToList();
        var bills = await db.Set<Bill>().AsNoTracking().Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.BillNumber, ct);
        var patients = await PatientQueries.SummariesAsync(db, refunds.Select(r => r.PatientId), BillingTime.Today(time), fullContacts: true, ct);
        var now = time.GetUtcNow();
        return [.. refunds.Select(r => ToPortal(r, r.BillId is { } bill ? bills.GetValueOrDefault(bill) : null, patients.GetValueOrDefault(r.PatientId)?.FullName, now))];
    }

    public static PortalRefund ToPortal(Refund refund, string? billNumber, string? patientName, DateTimeOffset now)
    {
        var stage = StageOf(refund, now);
        var throughGateway = refund.GatewayRefundId is not null;
        return new PortalRefund(
            refund.Id, refund.RefundNumber, refund.PatientId, patientName, refund.BillId, billNumber, refund.Purpose, refund.Amount, stage, refund.RequestedAt,
            SentAt: stage is PortalRefundStage.Requested or PortalRefundStage.Approved or PortalRefundStage.NotApproved
                ? null
                : throughGateway ? refund.GatewayRefundStartedAt ?? refund.PaidAt : refund.PaidAt,
            Mode: refund.Status == RefundStatus.Paid ? refund.Mode : null,
            Reference: stage switch
            {
                PortalRefundStage.Credited or PortalRefundStage.WithTheBank => refund.GatewayReference,
                PortalRefundStage.PaidOut when refund.Mode != PaymentMode.Cash => refund.Reference,
                _ => null,
            },
            CreditedAt: stage == PortalRefundStage.Credited ? refund.GatewaySettledAt : null);
    }
}
