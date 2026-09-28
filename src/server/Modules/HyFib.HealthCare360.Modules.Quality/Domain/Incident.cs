namespace HyFib.HealthCare360.Modules.Quality.Domain;

public enum IncidentType
{
    Clinical = 0,
    MedicationError,
    PatientFall,
    NeedleStick,
    EquipmentFailure,
    Security,
    Fire,
    Complaint,
    NearMiss,
    AdverseDrugReaction,
    HospitalAcquiredInfection,
    Other,
}

/// <summary>Harm grading used by NABH incident reporting, from no harm reached the patient to death.</summary>
public enum IncidentSeverity
{
    NearMiss = 0,
    NoHarm,
    Mild,
    Moderate,
    Severe,
    Death,
}

/// <summary>"Never events" that are sentinel regardless of the harm grade.</summary>
public enum SentinelEventKind
{
    WrongSiteSurgery = 0,
    WrongPatient,
    WrongProcedure,
    RetainedForeignObject,
    InfantAbductionOrWrongDischarge,
    PatientSuicide,
    AboIncompatibleTransfusion,
    SexualAssault,
    UnexpectedDeath,
    Other,
}

public enum IncidentStatus
{
    Reported = 0,
    UnderReview,
    Investigation,
    CapaInProgress,
    Closed,
}

/// <summary>
/// A reported incident or near miss (IRMS-01/02). Reporting is open to all staff and may be anonymous: then no
/// reporter identity is stored anywhere on the row (see <c>AnonymousReportingScope</c> for the audit trail).
/// Status changes go through <see cref="IncidentWorkflow"/> so the NABH review path cannot be skipped.
/// </summary>
public sealed class Incident : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}IR{YY}{SEQ:5}", e.g. NHIR2600017.</summary>
    public string IncidentNumber { get; set; } = string.Empty;

    public IncidentType Type { get; set; }
    public IncidentSeverity Severity { get; set; }
    public bool IsSentinel { get; set; }
    public SentinelEventKind? SentinelKind { get; set; }

    /// <summary>Why the incident is sentinel (death, severe harm, a never event, or flagged by a person).</summary>
    public string? SentinelReason { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
    public string Location { get; set; } = string.Empty;
    public Guid? DepartmentId { get; set; }

    /// <summary>Patient involved (Patients module id); optional because staff/visitor/property incidents have none.</summary>
    public Guid? PatientId { get; set; }

    /// <summary>Staff user accounts involved.</summary>
    public List<Guid> InvolvedStaffUserIds { get; set; } = [];

    public string Description { get; set; } = string.Empty;
    public string? ImmediateAction { get; set; }
    public string? Witnesses { get; set; }

    public bool IsAnonymous { get; set; }

    /// <summary>Null for anonymous reports.</summary>
    public Guid? ReporterUserId { get; set; }

    /// <summary>Null for anonymous reports.</summary>
    public string? ReporterName { get; set; }

    public IncidentStatus Status { get; set; }
    public Guid? AssignedToUserId { get; set; }
    public DateTimeOffset? ReviewStartedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosureNote { get; set; }
    public string? LastStatusNote { get; set; }
    public int ReopenCount { get; set; }

    /// <summary>(Re)applies the sentinel rules; a person's explicit flag is kept even when the grade is lowered.</summary>
    public void Classify(IncidentType type, IncidentSeverity severity, SentinelEventKind? sentinelKind, bool flaggedSentinel)
    {
        Type = type;
        Severity = severity;
        SentinelKind = sentinelKind;
        IsSentinel = SentinelRules.IsSentinel(severity, sentinelKind, flaggedSentinel);
        SentinelReason = SentinelRules.Reason(severity, sentinelKind, flaggedSentinel);
    }

    /// <summary>Moves along the review path; see <see cref="IncidentWorkflow"/> for the rules.</summary>
    public Result MoveTo(IncidentStatus target, string? note, IncidentProgress progress, DateTimeOffset now)
    {
        var check = IncidentWorkflow.CanMove(Status, target, IsSentinel, progress, note);
        if (check.IsFailure)
        {
            return check;
        }

        var reopening = Status == IncidentStatus.Closed;
        Status = target;
        LastStatusNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        switch (target)
        {
            case IncidentStatus.UnderReview when reopening:
                ReopenCount++;
                ClosedAt = null;
                ClosureNote = null;
                break;
            case IncidentStatus.UnderReview:
                ReviewStartedAt ??= now;
                break;
            case IncidentStatus.Closed:
                ClosedAt = now;
                ClosureNote = LastStatusNote;
                break;
        }

        return Result.Success();
    }

    public void MarkReported() => Raise(new IncidentReported(Id, IncidentNumber, FacilityId, Type, Severity, IsSentinel));
}

/// <summary>Raised on every new report (no reporter identity by design).</summary>
public sealed record IncidentReported(Guid IncidentId, string IncidentNumber, Guid FacilityId, IncidentType Type, IncidentSeverity Severity, bool IsSentinel) : DomainEvent;
