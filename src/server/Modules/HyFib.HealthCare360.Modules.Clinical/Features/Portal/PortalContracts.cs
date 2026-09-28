using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Portal;

public sealed record PortalPrescriptionListItem(
    Guid Id,
    string PrescriptionNumber,
    Guid PatientId,
    PrescriptionStatus Status,
    DateTimeOffset? SignedAt,
    string? Doctor,
    IReadOnlyList<string> Medicines,
    int RefillsRemaining);

public sealed record PortalPrescriptionLine(
    string DrugName,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string Dose,
    string Frequency,
    string FrequencyEnglish,
    string FrequencyHindi,
    int DurationDays,
    decimal Quantity,
    string? Instructions);

public sealed record PortalPrescriptionDetail(
    Guid Id,
    string PrescriptionNumber,
    Guid PatientId,
    PrescriptionStatus Status,
    DateTimeOffset? SignedAt,
    string? Doctor,
    string? DoctorQualification,
    string? DoctorRegistrationNumber,
    string? Facility,
    string? Advice,
    IReadOnlyList<PortalPrescriptionLine> Lines,
    int RefillsRemaining,
    string? VerificationToken);

/// <summary>An encounter as shown to the patient: when, who and the diagnoses — never the clinician's free-text notes.</summary>
public sealed record PortalEncounter(Guid Id, EncounterType Type, DateTimeOffset StartedAt, string? Doctor, string? Department, IReadOnlyList<string> Diagnoses, DateOnly? FollowUpDate);

public sealed record PortalImmunization(string VaccineName, int? DoseNumber, DateOnly AdministeredOn, DateOnly? NextDueOn);

public sealed record PortalDischargeSummaryItem(Guid Id, Guid AdmissionId, DateTimeOffset? DischargedAt, DateTimeOffset? SignedAt, string? Diagnoses);

public sealed record PortalHealthRecord(
    Guid PatientId,
    IReadOnlyList<AllergySummary> Allergies,
    IReadOnlyList<ProblemSummary> Problems,
    IReadOnlyList<PortalEncounter> RecentEncounters,
    IReadOnlyList<PortalImmunization> Immunizations,
    IReadOnlyList<PortalDischargeSummaryItem> DischargeSummaries);

/// <summary>A copy of records requested by the patient (PP-05); MRD processes it like any other record request.</summary>
public sealed record PortalRecordRequest(Guid PatientId, RecordRequestPurpose Purpose, string? Details);

public sealed class PortalRecordRequestValidator : AbstractValidator<PortalRecordRequest>
{
    public PortalRecordRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Details).SafeText(1000);
    }
}
