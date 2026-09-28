namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

public enum CssdRequestStatus
{
    Open = 0,
    PartiallyIssued,
    Issued,
    Cancelled,
}

public enum CssdPriority
{
    Routine = 0,
    Urgent,
}

/// <summary>A department's request for sterile sets (CSSD-02). Issues against it fill the lines until it is complete.</summary>
public sealed class CssdRequest : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public Guid DepartmentId { get; set; }
    public string? RequestedByName { get; set; }
    public DateTimeOffset? RequiredBy { get; set; }
    public CssdPriority Priority { get; set; }
    public CssdRequestStatus Status { get; set; }
    public string? Notes { get; set; }
    public string? CancelReason { get; set; }
    public List<CssdRequestLine> Lines { get; set; } = [];

    public bool IsOpen => Status is CssdRequestStatus.Open or CssdRequestStatus.PartiallyIssued;

    /// <summary>Counts one issued pack against the request; packs of sets not requested are not counted.</summary>
    public void RecordIssue(Guid instrumentSetId)
    {
        var line = Lines.FirstOrDefault(l => l.InstrumentSetId == instrumentSetId && l.IssuedQuantity < l.Quantity);
        if (line is not null)
        {
            line.IssuedQuantity++;
        }

        RefreshStatus();
    }

    public void RefreshStatus()
    {
        if (Status == CssdRequestStatus.Cancelled)
        {
            return;
        }

        Status = Lines.Count > 0 && Lines.All(l => l.IssuedQuantity >= l.Quantity) ? CssdRequestStatus.Issued
            : Lines.Any(l => l.IssuedQuantity > 0) ? CssdRequestStatus.PartiallyIssued
            : CssdRequestStatus.Open;
    }

    public Result Cancel(string reason)
    {
        if (!IsOpen)
        {
            return Error.Rule("cssd.request_closed", "This request is already closed.");
        }

        Status = CssdRequestStatus.Cancelled;
        CancelReason = reason.Trim();
        return Result.Success();
    }
}

public sealed class CssdRequestLine : Entity
{
    public Guid CssdRequestId { get; set; }
    public Guid InstrumentSetId { get; set; }
    public int Quantity { get; set; }
    public int IssuedQuantity { get; set; }
}
