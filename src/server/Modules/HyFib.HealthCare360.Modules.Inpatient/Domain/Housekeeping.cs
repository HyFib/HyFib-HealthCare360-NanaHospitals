namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum HousekeepingStatus
{
    Pending = 0,
    InProgress,
    Completed,
    Cancelled,
}

public enum HousekeepingTrigger
{
    Discharge = 0,
    Transfer,
    CancelledAdmission,
    Maintenance,
    Manual,
}

/// <summary>
/// Terminal cleaning of a bed after the patient leaves (WB-05). The time from <see cref="RequestedAt"/> to
/// <see cref="CompletedAt"/> is the housekeeping turnaround reported to management; the bed becomes Vacant only when the
/// task is completed, which is what keeps unclean beds off the admission desk's list.
/// </summary>
public sealed class HousekeepingTask : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid BedId { get; set; }
    public Guid WardId { get; set; }
    public Guid? AdmissionId { get; set; }
    public HousekeepingTrigger Trigger { get; set; }
    public HousekeepingStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public Guid? StartedBy { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedBy { get; set; }
    public int? TurnaroundMinutes { get; set; }
    public string? Notes { get; set; }

    public bool IsOpen => Status is HousekeepingStatus.Pending or HousekeepingStatus.InProgress;

    public Result Start(Guid? userId, DateTimeOffset now)
    {
        if (Status != HousekeepingStatus.Pending)
        {
            return Error.Rule("housekeeping.not_pending", "This cleaning task has already been started or closed.");
        }

        Status = HousekeepingStatus.InProgress;
        StartedAt = now;
        StartedBy = userId;
        return Result.Success();
    }

    public Result Complete(Guid? userId, string? notes, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("housekeeping.closed", "This cleaning task is already closed.");
        }

        Status = HousekeepingStatus.Completed;
        StartedAt ??= now;
        StartedBy ??= userId;
        CompletedAt = now;
        CompletedBy = userId;
        TurnaroundMinutes = (int)Math.Max(0, Math.Round((now - RequestedAt).TotalMinutes, MidpointRounding.AwayFromZero));
        Notes = string.IsNullOrWhiteSpace(notes) ? Notes : notes.Trim();
        return Result.Success();
    }

    /// <summary>The bed left the cleaning state some other way (for example it was sent for maintenance).</summary>
    public void Cancel(string? notes)
    {
        if (IsOpen)
        {
            Status = HousekeepingStatus.Cancelled;
            Notes = string.IsNullOrWhiteSpace(notes) ? Notes : notes.Trim();
        }
    }
}
