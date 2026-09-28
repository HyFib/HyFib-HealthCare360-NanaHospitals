namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum AssessmentType
{
    MorseFall = 0,
    Braden,
    Pain,
}

public enum RiskLevel
{
    None = 0,
    Low,
    Moderate,
    High,
    VeryHigh,
}

/// <summary>
/// A scored nursing risk assessment. The item responses are kept (JSON) next to the computed score so the score can be
/// re-derived and audited; the latest risk level of each type drives bed-board alerts (fall precautions, pressure-injury
/// prevention, pain reassessment).
/// </summary>
public sealed class NursingAssessment : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public AssessmentType Type { get; set; }
    public int Score { get; set; }
    public RiskLevel Risk { get; set; }
    public string Interpretation { get; set; } = string.Empty;
    public string Responses { get; set; } = "{}";
    public DateTimeOffset AssessedAt { get; set; }
    public Guid? AssessedBy { get; set; }
    public string? AssessedByName { get; set; }
    public string? Notes { get; set; }
}
