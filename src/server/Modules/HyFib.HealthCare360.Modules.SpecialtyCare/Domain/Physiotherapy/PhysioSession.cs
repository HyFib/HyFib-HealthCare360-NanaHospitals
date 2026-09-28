using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;

/// <summary>One scheduled physiotherapy session (PHY-03/04): attendance, modalities given, progress note, pain after.</summary>
public sealed class PhysioSession : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid ReferralId { get; set; }
    public Guid PatientId { get; set; }

    /// <summary>1-based position within the referral ("session 4 of 10").</summary>
    public int SessionNumber { get; set; }

    public DateTimeOffset ScheduledAt { get; set; }
    public PhysioSessionStatus Status { get; set; }
    public List<PhysioModality> ModalitiesGiven { get; set; } = [];
    public string? ProgressNote { get; set; }
    public int? PainBefore { get; set; }
    public int? PainAfter { get; set; }
    public int? DurationMinutes { get; set; }
    public DateTimeOffset? AttendedAt { get; set; }
    public Guid? TherapistUserId { get; set; }
    public string? TherapistName { get; set; }
    public string? MissedReason { get; set; }

    public ChargeStatus ChargeStatus { get; set; }
    public string? ChargeError { get; set; }

    public bool CountsAgainstPlan => Status != PhysioSessionStatus.Cancelled;

    /// <summary>Records the treatment given; a progress note is mandatory because it is the clinical record of the session.</summary>
    public Result Attend(IReadOnlyList<PhysioModality> modalities, string progressNote, int? painBefore, int? painAfter, int? durationMinutes, StaffStamp by)
    {
        if (Status != PhysioSessionStatus.Scheduled)
        {
            return Error.Rule("physio.session_closed", "This session has already been recorded.");
        }

        if (modalities.Count == 0)
        {
            return Error.Field("modalities", "Select the modalities given in this session.");
        }

        if (string.IsNullOrWhiteSpace(progressNote))
        {
            return Error.Field("progressNote", "Write a progress note for this session.");
        }

        Status = PhysioSessionStatus.Attended;
        ModalitiesGiven = modalities.Distinct().ToList();
        ProgressNote = progressNote.Trim();
        PainBefore = painBefore;
        PainAfter = painAfter;
        DurationMinutes = durationMinutes;
        AttendedAt = by.At;
        TherapistUserId = by.UserId;
        TherapistName = by.Name;
        return Result.Success();
    }

    public Result Reschedule(DateTimeOffset scheduledAt)
    {
        if (Status != PhysioSessionStatus.Scheduled)
        {
            return Error.Rule("physio.session_closed", "Only a scheduled session can be moved.");
        }

        ScheduledAt = scheduledAt;
        return Result.Success();
    }

    public Result Close(PhysioSessionStatus outcome, string reason)
    {
        if (outcome is not (PhysioSessionStatus.Missed or PhysioSessionStatus.Cancelled))
        {
            return Error.Rule("physio.invalid_outcome", "A session can only be closed as missed or cancelled.");
        }

        if (Status != PhysioSessionStatus.Scheduled)
        {
            return Error.Rule("physio.session_closed", "This session has already been recorded.");
        }

        Status = outcome;
        MissedReason = reason.Trim();
        return Result.Success();
    }

    public void RecordCharges(ChargeStatus status, string? error)
    {
        ChargeStatus = status;
        ChargeError = error;
    }
}
