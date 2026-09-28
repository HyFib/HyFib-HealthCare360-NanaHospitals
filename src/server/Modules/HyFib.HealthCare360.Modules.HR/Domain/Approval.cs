namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>State of a request that needs a manager's or HR's decision (leave, attendance regularisation).</summary>
public enum ApprovalStatus
{
    Pending = 0,
    Approved,
    Rejected,
    Cancelled,
}

/// <summary>
/// Shared approval rules so leave and regularisation requests behave identically: only pending requests can be
/// decided, nobody approves their own request, and a decision is final (a rejected request is re-applied, not re-opened).
/// </summary>
public static class ApprovalRules
{
    public static Result CanDecide(ApprovalStatus status, Guid? requesterUserId, Guid approverUserId)
    {
        if (status != ApprovalStatus.Pending)
        {
            return Error.Rule("approval.not_pending", "This request has already been decided or withdrawn.");
        }

        if (requesterUserId is { } requester && requester == approverUserId)
        {
            return Error.Forbidden("approval.self", "You cannot approve or reject your own request.");
        }

        return Result.Success();
    }
}
