namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

public enum SetIssueStatus
{
    Issued = 0,
    Used,
    Returned,
    Recalled,
}

/// <summary>
/// A sterile pack issued to a department (CSSD-04). It keeps the pack's load and expiry, and optionally the patient it
/// was used on, so a failed load can be traced to every department and patient it reached (CSSD-05).
/// </summary>
public sealed class SetIssue : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PackId { get; set; }
    public Guid LoadId { get; set; }
    public Guid InstrumentSetId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? CssdRequestId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public string? IssuedToName { get; set; }
    public DateOnly? SterileUntil { get; set; }
    public SetIssueStatus Status { get; set; }
    public Guid? PatientId { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? ReturnedAt { get; set; }
    public Guid? SoiledReceiptId { get; set; }
    public DateTimeOffset? RecallFlaggedAt { get; set; }
    public string? RecallReason { get; set; }
    public DateTimeOffset? RecallAcknowledgedAt { get; set; }
    public Guid? RecallAcknowledgedBy { get; set; }
    public string? RecallAcknowledgedByName { get; set; }
    public string? RecallAction { get; set; }

    public bool IsRecallOpen => RecallFlaggedAt is not null && RecallAcknowledgedAt is null;

    public Result RecordUse(Guid patientId, DateTimeOffset at, DateOnly today)
    {
        if (RecallFlaggedAt is not null)
        {
            return Error.Rule("cssd.pack_recalled", "This pack has been recalled. Do not use it; return it to CSSD.");
        }

        if (Status != SetIssueStatus.Issued)
        {
            return Error.Rule("cssd.issue_not_open", "Only an issued, unused pack can be recorded as used.");
        }

        if (SterileUntil is { } until && until < today)
        {
            return Error.Rule("cssd.pack_expired", "This pack's sterility has expired. Do not use it; return it for reprocessing.");
        }

        Status = SetIssueStatus.Used;
        PatientId = patientId;
        UsedAt = at;
        return Result.Success();
    }

    public void MarkReturned(Guid receiptId, DateTimeOffset at)
    {
        if (Status is SetIssueStatus.Issued or SetIssueStatus.Used or SetIssueStatus.Recalled)
        {
            Status = SetIssueStatus.Returned;
            ReturnedAt = at;
            SoiledReceiptId = receiptId;
        }
    }

    /// <summary>Flags the issue for recall; an unused pack is also marked recalled so it cannot be used any more.</summary>
    public void FlagRecall(string reason, DateTimeOffset now)
    {
        if (RecallFlaggedAt is not null)
        {
            return;
        }

        RecallFlaggedAt = now;
        RecallReason = reason;
        if (Status == SetIssueStatus.Issued)
        {
            Status = SetIssueStatus.Recalled;
        }
    }

    public Result AcknowledgeRecall(Guid? userId, string? userName, string action, DateTimeOffset now)
    {
        if (RecallFlaggedAt is null)
        {
            return Error.Rule("cssd.no_recall", "This pack has not been recalled.");
        }

        if (RecallAcknowledgedAt is not null)
        {
            return Error.Rule("cssd.recall_acknowledged", "This recall has already been acknowledged.");
        }

        RecallAcknowledgedAt = now;
        RecallAcknowledgedBy = userId;
        RecallAcknowledgedByName = userName;
        RecallAction = action.Trim();
        return Result.Success();
    }
}
