using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;

/// <summary>Goniometer reading for one joint movement (degrees).</summary>
public sealed record RangeOfMotion(string Joint, string Movement, Laterality Side, int? ActiveDegrees, int? PassiveDegrees);

/// <summary>Manual muscle test on the MRC scale (0 = no contraction … 5 = normal power).</summary>
public sealed record MuscleStrength(string MuscleGroup, Laterality Side, int MrcGrade);

/// <summary>
/// Physiotherapy assessment (PHY-01): complaint, pain (0–10 numeric rating scale), range of motion, strength,
/// functional status and goals. Initial, review and discharge assessments are kept so progress can be charted.
/// </summary>
public sealed class PhysioAssessment : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid ReferralId { get; set; }
    public Guid PatientId { get; set; }
    public PhysioAssessmentType Type { get; set; }
    public DateTimeOffset AssessedAt { get; set; }
    public string? AssessedByName { get; set; }
    public string ChiefComplaint { get; set; } = string.Empty;
    public int? PainScore { get; set; }
    public string? PainLocation { get; set; }
    public List<RangeOfMotion> RangeOfMotion { get; set; } = [];
    public List<MuscleStrength> Strength { get; set; } = [];
    public string? FunctionalStatus { get; set; }
    public string? Goals { get; set; }
    public string? Notes { get; set; }
}
