using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emr;

public sealed record AllergySummary(Guid Id, string Allergen, AllergyType Type, AllergySeverity Severity, string? Reaction);

public sealed record AlertSummary(Guid Id, PatientAlertType Type, string Title, string? Detail, AlertSeverity Severity, DateOnly? ExpiresOn);

public sealed record ProblemSummary(Guid Id, string? IcdCode, string Title, DiagnosisType Type, DiagnosisStatus Status, DateOnly? OnsetDate);

public sealed record CurrentMedication(
    Guid PrescriptionId,
    string PrescriptionNumber,
    Guid ItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string Dose,
    string Frequency,
    int DurationDays,
    DateOnly StartedOn,
    DateOnly EndsOn,
    Guid PractitionerId,
    string? PrescriberName,
    bool IsControlledDrug);

public sealed record VitalsSnapshot(
    Guid Id,
    DateTimeOffset RecordedAt,
    decimal? TemperatureCelsius,
    int? Pulse,
    int? RespiratoryRate,
    int? SystolicBp,
    int? DiastolicBp,
    int? SpO2,
    decimal? WeightKg,
    decimal? Bmi,
    int? PainScore,
    decimal? BloodGlucose,
    int? Gcs,
    IReadOnlyList<VitalFlag> Flags);

public sealed record RecentEncounter(
    Guid Id,
    EncounterType Type,
    EncounterStatus Status,
    DateTimeOffset StartedAt,
    string? PractitionerName,
    string? DepartmentName,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnoses);

/// <summary>
/// Everything the persistent EMR banner and summary panel need in one call (EHR-03, UX-04): allergies (with
/// "never recorded" distinguished from "none active"), current critical/infection-control alerts, active problems,
/// current medications, the last vitals with their flags and the last five encounters.
/// </summary>
public sealed record PatientClinicalSummary(
    PatientSummary Patient,
    bool AllergiesEverRecorded,
    IReadOnlyList<AllergySummary> Allergies,
    IReadOnlyList<AlertSummary> Alerts,
    IReadOnlyList<ProblemSummary> ActiveProblems,
    IReadOnlyList<CurrentMedication> CurrentMedications,
    VitalsSnapshot? LastVitals,
    IReadOnlyList<RecentEncounter> RecentEncounters,
    int ResultsAwaitingReview);

public enum TimelineEntryType
{
    Encounter = 0,
    Vitals,
    Diagnosis,
    Prescription,
    Order,
    Note,
    Immunization,
    Document,
    DischargeSummary,
    Result,
}

/// <summary>One row of the longitudinal EHR timeline (EHR-01/02).</summary>
public sealed record TimelineEntry(
    TimelineEntryType Type,
    Guid Id,
    DateTimeOffset OccurredAt,
    string Title,
    string? Summary,
    string? Status,
    Guid? EncounterId,
    bool IsCritical);
