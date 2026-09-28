namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum DiagnosisType
{
    Provisional = 0,
    Final,
    Differential,
}

public enum DiagnosisStatus
{
    Active = 0,
    Resolved,
    Chronic,
}

/// <summary>
/// A diagnosis made in an encounter, which also forms the patient's problem list (active and chronic problems,
/// excluding differentials). ICD-10 codes come from the local catalogue; free-text titles are allowed for
/// working diagnoses but coders replace them at discharge.
/// </summary>
public sealed class Diagnosis : AuditableEntity
{
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public string? IcdCode { get; set; }
    public string Title { get; set; } = string.Empty;
    public DiagnosisType Type { get; set; }
    public bool IsPrimary { get; set; }
    public DateOnly? OnsetDate { get; set; }
    public DiagnosisStatus Status { get; private set; }
    public DateOnly? ResolvedOn { get; private set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? Notes { get; set; }

    /// <summary>Counts towards the problem list shown in the banner and summary.</summary>
    public bool IsActiveProblem => Type != DiagnosisType.Differential && Status is DiagnosisStatus.Active or DiagnosisStatus.Chronic;

    public void ChangeStatus(DiagnosisStatus status, DateOnly today)
    {
        Status = status;
        ResolvedOn = status == DiagnosisStatus.Resolved ? ResolvedOn ?? today : null;
    }
}
