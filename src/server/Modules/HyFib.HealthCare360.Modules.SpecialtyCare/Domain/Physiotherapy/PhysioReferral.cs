namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;

/// <summary>
/// A request for physiotherapy (PHY-01): created automatically from an EMR physiotherapy order
/// (<c>ServiceOrderPlaced</c>) or manually by a doctor/physiotherapist. Anchors assessments, the plan and sessions.
/// </summary>
public sealed class PhysioReferral : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }

    /// <summary>EMR order line that created this referral (unique; makes the order handler idempotent).</summary>
    public Guid? OrderItemId { get; set; }

    public string? OrderNumber { get; set; }
    public Guid? ServiceItemId { get; set; }
    public string? ServiceName { get; set; }

    /// <summary>Sessions prescribed on the order (order quantity); the default number of sessions for the treatment plan.</summary>
    public int? SessionsOrdered { get; set; }
    public Guid? ReferredByPractitionerId { get; set; }
    public OrderPriority Priority { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset ReferredAt { get; set; }
    public PhysioReferralStatus Status { get; set; }
    public Guid? AssignedTherapistUserId { get; set; }
    public string? AssignedTherapistName { get; set; }
    public string? DischargeSummary { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? CancelReason { get; set; }

    public bool IsOpen => Status is not (PhysioReferralStatus.Completed or PhysioReferralStatus.Cancelled);

    public Result EnsureOpen() =>
        IsOpen ? Result.Success() : Error.Rule("physio.referral_closed", "This physiotherapy referral is closed.");

    public void MarkAssessed()
    {
        if (Status == PhysioReferralStatus.New)
        {
            Status = PhysioReferralStatus.Assessed;
        }
    }

    /// <summary>A treatment plan starts treatment; it needs an initial assessment first (no treatment without assessment).</summary>
    public Result StartTreatment()
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (Status == PhysioReferralStatus.New)
        {
            return Error.Rule("physio.assessment_required", "Assess the patient before planning treatment.");
        }

        Status = PhysioReferralStatus.InTreatment;
        return Result.Success();
    }

    public Result Complete(string summary, DateTimeOffset now)
    {
        if (Status is not (PhysioReferralStatus.Assessed or PhysioReferralStatus.InTreatment))
        {
            return Error.Rule("physio.not_in_treatment", "Only an assessed or ongoing referral can be completed.");
        }

        Status = PhysioReferralStatus.Completed;
        DischargeSummary = summary.Trim();
        ClosedAt = now;
        return Result.Success();
    }

    /// <summary>Cancelling is refused once any session was given (the referral must then be completed instead).</summary>
    public Result Cancel(string reason, bool hasAttendedSessions, DateTimeOffset now)
    {
        var open = EnsureOpen();
        if (open.IsFailure)
        {
            return open;
        }

        if (hasAttendedSessions)
        {
            return Error.Rule("physio.has_sessions", "Treatment has already been given; complete the referral instead of cancelling it.");
        }

        Status = PhysioReferralStatus.Cancelled;
        CancelReason = reason.Trim();
        ClosedAt = now;
        return Result.Success();
    }
}
