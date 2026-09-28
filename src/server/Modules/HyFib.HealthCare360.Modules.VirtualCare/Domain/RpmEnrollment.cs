namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

public enum RpmEnrollmentStatus
{
    Active = 0,

    /// <summary>Temporarily not monitored (travel, admission): readings are kept but raise no alerts.</summary>
    Paused,

    Completed,
    Withdrawn,
}

/// <summary>
/// A patient enrolled in a monitoring programme with a care team (RPM-01). Thresholds and devices belong to the
/// enrolment, so the same patient can be in two programmes (for example diabetes and hypertension) with different
/// limits and care teams.
/// </summary>
public sealed class RpmEnrollment : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProgramId { get; set; }

    /// <summary>Doctors and nurses who receive this patient's alerts.</summary>
    public List<Guid> CareTeamPractitionerIds { get; set; } = [];

    public Guid? PrimaryPractitionerId { get; set; }
    public RpmEnrollmentStatus Status { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string? Goals { get; set; }
    public string? Notes { get; set; }
    public string? StatusReason { get; set; }
    public DateTimeOffset? LastReadingAt { get; set; }
    public DateTimeOffset? ThresholdsUpdatedAt { get; set; }

    public bool IsClosed => Status is RpmEnrollmentStatus.Completed or RpmEnrollmentStatus.Withdrawn;

    /// <summary>Only active enrolments raise alerts; paused ones still store readings so nothing is lost.</summary>
    public bool RaisesAlerts => Status == RpmEnrollmentStatus.Active;

    public Result ChangeStatus(RpmEnrollmentStatus next, string? reason, DateOnly today)
    {
        if (next == Status)
        {
            return Result.Success();
        }

        if (IsClosed)
        {
            return Error.Rule("rpm.enrollment_closed", "This enrolment has ended. Enrol the patient again to restart monitoring.");
        }

        if (next == RpmEnrollmentStatus.Withdrawn && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("statusReason", "Give a reason for withdrawing the patient from monitoring.");
        }

        if (next is RpmEnrollmentStatus.Completed or RpmEnrollmentStatus.Withdrawn)
        {
            EndDate ??= today;
        }

        Status = next;
        StatusReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return Result.Success();
    }

    public void RecordReading(DateTimeOffset measuredAt)
    {
        if (LastReadingAt is null || measuredAt > LastReadingAt)
        {
            LastReadingAt = measuredAt;
        }
    }

    public void TouchThresholds(DateTimeOffset now) => ThresholdsUpdatedAt = now;
}
