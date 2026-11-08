using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Rounds;

/// <summary>
/// One in-patient on the ward round, with what needs the doctor's attention. "Seen", "note" and results are the
/// doctor's own when the round is a doctor's, and anyone's when it is a department's. For a restricted (VIP) patient the
/// name reads "Restricted patient", and the diagnosis and vital-sign details are left out, unless the user may open
/// restricted records; for a patient in a sensitive category the user does not hold, the diagnosis and vital-sign details
/// are left out (<see cref="IsConfidential"/>).
/// </summary>
public sealed record WardRoundPatient(
    Guid AdmissionId,
    string AdmissionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    Guid? WardId,
    string? WardName,
    string? BedLabel,
    bool IsCriticalCare,
    AdmissionStatus Status,
    DateTimeOffset AdmittedAt,
    int DayOfStay,
    DateOnly? ExpectedDischargeDate,
    string? ProvisionalDiagnosis,
    bool IsMlc,
    bool IsRestricted,
    Guid? ConsultantId,
    string? ConsultantName,
    bool IsPrimaryConsultant,
    bool HasActiveAllergies,
    bool HasCriticalAlerts,
    DateTimeOffset? LastVisitAt,
    bool SeenToday,
    DateTimeOffset? LastNoteAt,
    int ResultsToReview,
    bool HasCriticalResults,
    DateTimeOffset? VitalsAt,
    FlagSeverity? VitalsSeverity,
    IReadOnlyList<string> VitalsFlags,
    int? News2Score = null,
    News2Response? News2Response = null,
    bool News2Complete = false,
    bool HasOpenEscalation = false)
{
    /// <summary>
    /// A record in a sensitive category the user does not hold (HIV, mental health, MTP, sexual violence): named, as on the
    /// bed board, without the diagnosis and vital-sign details.
    /// </summary>
    public bool IsConfidential { get; init; }
}

/// <summary>
/// The ward round (EMR-01 "IP rounds"): the in-patients under a doctor's care (their own admissions and those they have
/// visited during the stay) or a department's, in the ward's walking order.
/// </summary>
public sealed record WardRound(DateOnly Date, Guid? PractitionerId, Guid? DepartmentId, IReadOnlyList<WardRoundPatient> Patients);
