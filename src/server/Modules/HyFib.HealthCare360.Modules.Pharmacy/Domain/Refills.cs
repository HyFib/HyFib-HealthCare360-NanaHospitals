namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

public enum RefillStatus
{
    Requested = 0,
    Approved,
    Rejected,
    Cancelled,
}

public enum RefillChannel
{
    Portal = 0,
    Staff,
}

/// <summary>
/// A request to refill a chronic prescription (PH-05, PP-05), raised by the patient on the portal or by staff.
/// Approval uses one of the prescription's authorised refills and queues a new entry for dispensing.
/// </summary>
public sealed class RefillRequest : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid QueueEntryId { get; set; }
    public Guid PatientId { get; set; }
    public RefillChannel Channel { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateOnly? PreferredPickupDate { get; set; }
    public string? Notes { get; set; }
    public RefillStatus Status { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? RejectReason { get; private set; }

    /// <summary>Queue entry created on approval (what the pharmacist dispenses).</summary>
    public Guid? RefillEntryId { get; private set; }

    public Result Approve(Guid? userId, Guid refillEntryId, DateTimeOffset now)
    {
        if (Status != RefillStatus.Requested)
        {
            return Error.Rule("refill.decided", $"Refill request {Number} is already {Status}.");
        }

        Status = RefillStatus.Approved;
        DecidedByUserId = userId;
        DecidedAt = now;
        RefillEntryId = refillEntryId;
        return Result.Success();
    }

    public Result Reject(Guid? userId, string reason, DateTimeOffset now)
    {
        if (Status != RefillStatus.Requested)
        {
            return Error.Rule("refill.decided", $"Refill request {Number} is already {Status}.");
        }

        Status = RefillStatus.Rejected;
        DecidedByUserId = userId;
        DecidedAt = now;
        RejectReason = reason;
        return Result.Success();
    }

    /// <summary>The prescription was cancelled by the doctor, so pending requests lapse.</summary>
    public void Lapse(string reason, DateTimeOffset now)
    {
        if (Status == RefillStatus.Requested)
        {
            Status = RefillStatus.Cancelled;
            DecidedAt = now;
            RejectReason = reason;
        }
    }
}
