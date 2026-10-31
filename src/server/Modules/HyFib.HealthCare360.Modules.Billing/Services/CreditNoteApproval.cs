using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>The credit note or write-off asked for, exactly as it will be issued once approved.</summary>
public sealed record CreditNotePayload(Guid BillId, CreditNoteKind Kind, decimal Amount, string Reason);

/// <summary>
/// A credit note on a finalized bill (MB-05, audit finding S6): the cashier or the billing desk asks for it, and an
/// approver, never the same person, issues it by approving the request. It lowers what the patient owes, so one person
/// alone could otherwise take money a patient paid in cash and cover it with a credit note.
/// </summary>
internal sealed class CreditNoteApproval(RefundService refunds) : IApprovalAction
{
    public const string Key = "billing.credit_note";

    public string Kind => Key;

    public string Title => "Credit note";

    public string ApprovePermission => BillingPermissions.RefundsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => refunds.IssueApprovedCreditNoteAsync(request, ct);

    public static string KindOf(CreditNoteKind kind) => kind == CreditNoteKind.WriteOff ? WriteOffApproval.Key : Key;

    public static string Words(CreditNoteKind kind) => kind == CreditNoteKind.WriteOff ? "Write-off" : "Credit note";
}

/// <summary>A write-off of a due that cannot be recovered: asked for and approved as a credit note is.</summary>
internal sealed class WriteOffApproval(RefundService refunds) : IApprovalAction
{
    public const string Key = "billing.write_off";

    public string Kind => Key;

    public string Title => "Write-off";

    public string ApprovePermission => BillingPermissions.RefundsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => refunds.IssueApprovedCreditNoteAsync(request, ct);
}
