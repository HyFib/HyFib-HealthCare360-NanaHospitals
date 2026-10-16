using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

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

/// <summary>
/// A prescription as the patient sees it. <see cref="VerificationUrl"/> is the QR address for the printed copy
/// (<c>{portal.baseUrl}/portal/verify/prescription/{token}</c>); both it and the token are null unless the
/// prescription is signed and <see cref="FinalCopyAvailable"/>, and the URL is null while <c>portal.baseUrl</c> is not
/// configured. <see cref="FinalCopyAvailable"/> is false while the hospital requires the doctor's DSC (ERX-04) and the
/// prescription has no valid one: it is then not printed, downloaded or shown as a QR code, and
/// <see cref="DigitalSignatureState"/> says why (<see cref="Prescriptions.DigitalSignatureState.NotSigned"/>: the
/// doctor has not signed it with their DSC yet).
/// </summary>
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
    string? VerificationToken,
    string? VerificationUrl,
    bool FinalCopyAvailable = true,
    DigitalSignatureState DigitalSignatureState = DigitalSignatureState.NotSigned);

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
