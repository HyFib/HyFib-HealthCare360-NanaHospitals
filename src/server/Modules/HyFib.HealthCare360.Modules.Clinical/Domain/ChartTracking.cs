using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum MedicalRecordStatus
{
    Incomplete = 0,
    Complete,
    Deficient,
    Archived,
}

public enum DeficiencyType
{
    MissingDischargeSummary = 0,
    UnsignedEncounter,
    UnsignedNote,
    MissingDiagnosis,
    MissingConsent,
    MissingSignature,
    PendingCoding,
    Other,
}

/// <summary>
/// MRD chart of one admission or encounter (MRD-03/05): completeness, deficiencies assigned to doctors, completion
/// and archival with a retention date (PRV-02: OP 3 years, IP 5 years, medico-legal cases permanently). A paper chart
/// tracks a file that exists only on paper (an episode from before the system, or written up by hand when it was down):
/// it has the file's own number and the episode's dates instead of an admission or encounter, and is issued, returned,
/// completed, archived and kept like any other chart.
/// </summary>
public sealed class MedicalRecord : AuditableEntity, IFacilityScoped
{
    /// <summary>The shortest retention the SRS allows; the schedule in force comes from <see cref="RecordRetention"/>.</summary>
    public const int OutPatientRetentionYears = RecordRetention.MinimumOutpatientYears;

    public const int InPatientRetentionYears = RecordRetention.MinimumInpatientYears;

    public const int PaperFileNumberMaxLength = 40;

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? VisitId { get; set; }

    /// <summary>The number written on a paper chart's file (its old IP or MRD number); null for a chart of the electronic record.</summary>
    public string? PaperFileNumber { get; set; }

    /// <summary>A paper chart of an in-patient stay (kept as long as in-patient records); otherwise an out-patient episode.</summary>
    public bool IsPaperInpatient { get; set; }

    /// <summary>A paper chart's episode: the visit or admission day, and the discharge day of a stay.</summary>
    public DateOnly? PaperEpisodeFrom { get; set; }

    public DateOnly? PaperEpisodeTo { get; set; }

    /// <summary>What the paper file holds (volumes, department, condition), as MRD wrote it.</summary>
    public string? PaperNote { get; set; }

    public DateTimeOffset? AdmittedAt { get; set; }
    public DateTimeOffset? DischargedAt { get; set; }
    public MedicalRecordStatus Status { get; private set; }
    public Guid? AssignedPractitionerId { get; set; }

    /// <summary>Date by which the chart must be complete (hospital policy after discharge).</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>Medico-legal case: retained permanently.</summary>
    public bool IsMlc { get; set; }

    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid? CompletedByUserId { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public string? ArchiveLocation { get; private set; }

    /// <summary>Earliest date the record may be destroyed; null = keep permanently.</summary>
    public DateOnly? RetainUntil { get; private set; }

    public List<ChartDeficiency> Deficiencies { get; set; } = [];

    public bool IsPaper => PaperFileNumber is not null;

    public bool IsInpatient => AdmissionId is not null || IsPaperInpatient;

    public int OpenDeficiencyCount => Deficiencies.Count(d => d.IsOpen);

    public Result<ChartDeficiency> AddDeficiency(DeficiencyType type, string description, Guid? practitionerId, DateOnly? dueDate,
        string? reference, Guid? raisedByUserId, bool systemDetected, DateTimeOffset now)
    {
        if (Status == MedicalRecordStatus.Archived)
        {
            return Error.Rule("chart.archived", "This chart is archived.");
        }

        var deficiency = new ChartDeficiency
        {
            MedicalRecordId = Id,
            Type = type,
            Description = description.Trim(),
            Reference = reference,
            AssignedPractitionerId = practitionerId ?? AssignedPractitionerId,
            DueDate = dueDate ?? DueDate,
            RaisedAt = now,
            RaisedByUserId = raisedByUserId,
            IsSystemDetected = systemDetected,
        };
        Deficiencies.Add(deficiency);
        Status = MedicalRecordStatus.Deficient;
        CompletedAt = null;
        CompletedByUserId = null;
        return deficiency;
    }

    public bool HasOpenDeficiency(DeficiencyType type, string? reference) =>
        Deficiencies.Any(d => d.IsOpen && d.Type == type && d.Reference == reference);

    public Result ResolveDeficiency(Guid deficiencyId, Guid? userId, string? note, DateTimeOffset now)
    {
        var deficiency = Deficiencies.FirstOrDefault(d => d.Id == deficiencyId);
        if (deficiency is null)
        {
            return Error.NotFound("Deficiency", deficiencyId);
        }

        if (!deficiency.IsOpen)
        {
            return Error.Rule("deficiency.resolved", "This deficiency is already resolved.");
        }

        deficiency.ResolvedAt = now;
        deficiency.ResolvedByUserId = userId;
        deficiency.ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (OpenDeficiencyCount == 0 && Status == MedicalRecordStatus.Deficient)
        {
            // Resolution does not complete the chart: the MRD officer reviews and completes it explicitly.
            Status = MedicalRecordStatus.Incomplete;
        }

        return Result.Success();
    }

    /// <summary>
    /// Completes the chart and dates its retention: <paramref name="referenceDate"/> (discharge, or completion for an
    /// out-patient chart) plus the period in force for its kind.
    /// </summary>
    public Result Complete(Guid? userId, DateTimeOffset now, DateOnly referenceDate,
        int outPatientYears = OutPatientRetentionYears, int inPatientYears = InPatientRetentionYears)
    {
        if (Status is MedicalRecordStatus.Complete or MedicalRecordStatus.Archived)
        {
            return Error.Rule("chart.complete", "This chart is already complete.");
        }

        if (OpenDeficiencyCount > 0)
        {
            return Error.Rule("chart.deficient", $"Resolve the {OpenDeficiencyCount} open deficienc{(OpenDeficiencyCount == 1 ? "y" : "ies")} first.");
        }

        Status = MedicalRecordStatus.Complete;
        CompletedAt = now;
        CompletedByUserId = userId;
        RetainUntil = RetentionUntil(IsInpatient, IsMlc, referenceDate, outPatientYears, inPatientYears);
        return Result.Success();
    }

    public Result Archive(string location, DateTimeOffset now)
    {
        if (Status != MedicalRecordStatus.Complete)
        {
            return Error.Rule("chart.not_complete", "Only complete charts can be archived.");
        }

        Status = MedicalRecordStatus.Archived;
        ArchivedAt = now;
        ArchiveLocation = location.Trim();
        return Result.Success();
    }

    /// <summary>Retention rule (PRV-02); a null result means the record must never be destroyed.</summary>
    public static DateOnly? RetentionUntil(bool isInpatient, bool isMlc, DateOnly referenceDate,
        int outPatientYears = OutPatientRetentionYears, int inPatientYears = InPatientRetentionYears) =>
        isMlc ? null : referenceDate.AddYears(isInpatient ? inPatientYears : outPatientYears);
}

public sealed class ChartDeficiency : Entity
{
    public Guid MedicalRecordId { get; set; }
    public DeficiencyType Type { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>What the deficiency points at ("encounter:{id}", "note:{id}", "discharge-summary").</summary>
    public string? Reference { get; set; }

    public Guid? AssignedPractitionerId { get; set; }
    public DateOnly? DueDate { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public Guid? RaisedByUserId { get; set; }
    public bool IsSystemDetected { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public string? ResolutionNote { get; set; }

    public bool IsOpen => ResolvedAt is null;
}

public enum RecordRequestStatus
{
    Requested = 0,
    Issued,
    Returned,

    /// <summary>Computed: issued and not returned by the due date.</summary>
    Overdue,
    Cancelled,
}

public enum RecordRequestPurpose
{
    PatientCare = 0,
    Legal,
    Insurance,
    Research,
    Audit,
    PatientCopy,
    Other,
}

/// <summary>Request to issue a physical/electronic chart and track its return (MRD-04).</summary>
public sealed class RecordRequest : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string RequestNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? MedicalRecordId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public Guid? RequestedByUserId { get; set; }
    public string? RequestingDepartment { get; set; }
    public RecordRequestPurpose Purpose { get; set; }
    public string? Details { get; set; }

    /// <summary>Raised by the patient through the portal (PP-05).</summary>
    public bool IsPatientRequest { get; set; }

    public RecordRequestStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? IssuedAt { get; private set; }
    public Guid? IssuedByUserId { get; private set; }
    public DateOnly? DueBackOn { get; private set; }
    public DateTimeOffset? ReturnedAt { get; private set; }
    public Guid? ReceivedByUserId { get; private set; }
    public string? CancelReason { get; private set; }

    public RecordRequestStatus EffectiveStatus(DateOnly today) =>
        Status == RecordRequestStatus.Issued && DueBackOn is { } due && due < today ? RecordRequestStatus.Overdue : Status;

    public Result Issue(DateOnly? dueBackOn, Guid? userId, DateTimeOffset now)
    {
        if (Status != RecordRequestStatus.Requested)
        {
            return Error.Rule("record_request.not_pending", "Only pending requests can be issued.");
        }

        Status = RecordRequestStatus.Issued;
        IssuedAt = now;
        IssuedByUserId = userId;
        DueBackOn = dueBackOn;
        return Result.Success();
    }

    public Result Return(Guid? userId, DateTimeOffset now)
    {
        if (Status != RecordRequestStatus.Issued)
        {
            return Error.Rule("record_request.not_issued", "Only issued records can be returned.");
        }

        Status = RecordRequestStatus.Returned;
        ReturnedAt = now;
        ReceivedByUserId = userId;
        return Result.Success();
    }

    public Result Cancel(string reason)
    {
        if (Status != RecordRequestStatus.Requested)
        {
            return Error.Rule("record_request.not_pending", "Only pending requests can be cancelled.");
        }

        Status = RecordRequestStatus.Cancelled;
        CancelReason = reason.Trim();
        return Result.Success();
    }
}
