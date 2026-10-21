using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emr;

public sealed record AllergySummary(Guid Id, string Allergen, AllergyType Type, AllergySeverity Severity, string? Reaction);

public sealed record AlertSummary(Guid Id, PatientAlertType Type, string Title, string? Detail, AlertSeverity Severity, DateOnly? ExpiresOn);

/// <summary>
/// The patient banner's safety flags alone (EHR-03): what may be claimed about allergies, the active allergies (most
/// severe first) and the current alerts (most severe first). The same values as in <see cref="PatientClinicalSummary"/>,
/// for screens whose staff may see the flags but not the chart.
/// </summary>
public sealed record PatientFlags(AllergyRecordingStatus AllergyStatus, IReadOnlyList<AllergySummary> Allergies, IReadOnlyList<AlertSummary> Alerts);

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
/// current medications, the last vitals with their flags and the last five encounters. <see cref="AllergyStatus"/> is
/// what the banner may claim: <c>Recorded</c>, <c>NoKnownAllergies</c> (only when a clinician recorded it) or
/// <c>NotRecorded</c>.
/// </summary>
public sealed record PatientClinicalSummary(
    PatientSummary Patient,
    bool AllergiesEverRecorded,
    AllergyRecordingStatus AllergyStatus,
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
    Procedure,
}

/// <summary>
/// One row of the longitudinal EHR timeline (EHR-01/02). For a <see cref="TimelineEntryType.Result"/>, <see cref="Id"/>
/// is the released report's id in its department (the lab report, or the imaging or cardiology report) and
/// <see cref="ReportKind"/> says which, so the chart opens the report, and an imaging study's images, through the
/// department's own endpoints (IMG-02, IMG-03).
/// </summary>
public sealed record TimelineEntry(
    TimelineEntryType Type,
    Guid Id,
    DateTimeOffset OccurredAt,
    string Title,
    string? Summary,
    string? Status,
    Guid? EncounterId,
    bool IsCritical)
{
    /// <summary>The department of a result's report; null for every other type of entry.</summary>
    public DiagnosticKind? ReportKind { get; init; }
}
