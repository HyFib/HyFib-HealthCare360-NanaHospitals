using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

// Read-only snapshots of the rows each FHIR resource is built from. The data layer projects EF queries into them; the
// mappers turn them into FHIR resources without touching the database, so every mapping rule is unit-testable.
// CreatedAt/UpdatedAt are the entities' audit columns and become meta.lastUpdated.

/// <summary>Patients module <see cref="Patient"/> (demographics; ABHA already decrypted by the data layer).</summary>
internal sealed record PatientRow
{
    public required Guid Id { get; init; }
    public required string Uhid { get; init; }
    public string? Title { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string? MiddleName { get; init; }
    public string? LastName { get; init; }
    public string FullName { get; init; } = string.Empty;
    public Gender Gender { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public bool IsDateOfBirthEstimated { get; init; }
    public MaritalStatus MaritalStatus { get; init; }
    public string? Phone { get; init; }
    public string? AlternatePhone { get; init; }
    public string? Email { get; init; }
    public Address Address { get; init; } = new();
    public string? GuardianName { get; init; }
    public GuardianRelation? GuardianRelation { get; init; }
    public string? EmergencyContactName { get; init; }
    public string? EmergencyContactPhone { get; init; }
    public string? EmergencyContactRelation { get; init; }
    public string? PreferredLanguage { get; init; }

    /// <summary>Plain 14-digit ABHA number, decrypted for this response only (never logged).</summary>
    public string? AbhaNumber { get; init; }

    public bool IsDeceased { get; init; }
    public DateTimeOffset? DeceasedAt { get; init; }
    public bool IsActive { get; init; } = true;
    public Guid? MergedIntoPatientId { get; init; }
    public Guid RegisteredFacilityId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Inpatient module <see cref="Admission"/> of an in-patient visit.</summary>
internal sealed record AdmissionRow
{
    public required Guid Id { get; init; }
    public string AdmissionNumber { get; init; } = string.Empty;
    public AdmissionType Type { get; init; }
    public AdmissionSource Source { get; init; }
    public AdmissionStatus Status { get; init; }
    public DateTimeOffset AdmittedAt { get; init; }
    public Guid? AdmittingPractitionerId { get; init; }
    public Guid? ConsultantPractitionerId { get; init; }
    public Guid? DepartmentId { get; init; }
    public DischargeType? DischargeType { get; init; }
    public DateTimeOffset? DischargedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Patients module <see cref="Visit"/>, with its admission when it is an in-patient stay.</summary>
internal sealed record VisitRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid FacilityId { get; init; }
    public string VisitNumber { get; init; } = string.Empty;
    public VisitType Type { get; init; }
    public VisitStatus Status { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? PractitionerId { get; init; }
    public DateTimeOffset VisitedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ChiefComplaint { get; init; }
    public AdmissionRow? Admission { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Clinical module encounter (the clinician's documented contact within a visit or an admission).</summary>
internal sealed record ClinicalEncounterRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid FacilityId { get; init; }
    public Guid? VisitId { get; init; }
    public Guid? AdmissionId { get; init; }
    public Guid PractitionerId { get; init; }
    public Guid? DepartmentId { get; init; }
    public EncounterType Type { get; init; }
    public EncounterStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? SignedAt { get; init; }
    public string? ChiefComplaint { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Clinical module <see cref="VitalSigns"/> set (one row, several observations).</summary>
internal sealed record VitalSignsRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid? EncounterId { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public decimal? Temperature { get; init; }
    public TemperatureUnit TemperatureUnit { get; init; }
    public int? Pulse { get; init; }
    public int? RespiratoryRate { get; init; }
    public int? SystolicBp { get; init; }
    public int? DiastolicBp { get; init; }
    public int? SpO2 { get; init; }
    public bool OnSupplementalOxygen { get; init; }
    public decimal? HeightCm { get; init; }
    public decimal? WeightKg { get; init; }
    public decimal? Bmi { get; init; }
    public int? PainScore { get; init; }
    public decimal? BloodGlucose { get; init; }
    public GlucoseTiming GlucoseTiming { get; init; }
    public int? Gcs { get; init; }
    public bool IsEnteredInError { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Diagnostics module <see cref="LabResult"/> of a released test, with its test master's LOINC code.</summary>
internal sealed record LabResultRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid LabOrderId { get; init; }
    public Guid? VisitId { get; init; }
    public string TestCode { get; init; } = string.Empty;
    public string TestName { get; init; } = string.Empty;
    public string? Loinc { get; init; }

    /// <summary>The LOINC name of <see cref="Loinc"/> in the test master (the code's display), when the laboratory gave it.</summary>
    public string? LoincName { get; init; }

    public LabResultType ResultType { get; init; }
    public string? ValueText { get; init; }
    public decimal? NumericValue { get; init; }
    public string? Unit { get; init; }
    public ResultFlag? Flag { get; init; }
    public string? ReferenceText { get; init; }
    public string? Method { get; init; }
    public string? Remarks { get; init; }
    public string? Organism { get; init; }
    public string? ColonyCount { get; init; }

    /// <summary>The referral laboratory the test was sent out to, which performed it (LIS-08); null when done in-house.</summary>
    public Guid? ReferralLabId { get; init; }

    public DateTimeOffset OrderedAt { get; init; }
    public DateTimeOffset? CollectedAt { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>When its test was released; an amendment of the result does not change it.</summary>
    public DateTimeOffset? ReleasedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Diagnostics module <see cref="ReferralLab"/>: an outside laboratory that performs the tests sent out to it.</summary>
internal sealed record ReferralLabRow
{
    public required Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Address { get; init; }

    /// <summary>Its NABL accreditation number.</summary>
    public string? AccreditationNumber { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Clinical module <see cref="Diagnosis"/> (encounter diagnosis and problem list).</summary>
internal sealed record DiagnosisRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid? EncounterId { get; init; }
    public string? IcdCode { get; init; }
    public string Title { get; init; } = string.Empty;
    public DiagnosisType Type { get; init; }
    public bool IsPrimary { get; init; }
    public DateOnly? OnsetDate { get; init; }
    public DiagnosisStatus Status { get; init; }
    public DateOnly? ResolvedOn { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Clinical module <see cref="Allergy"/>.</summary>
internal sealed record AllergyRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public string Allergen { get; init; } = string.Empty;
    public AllergyType Type { get; init; }
    public string? Reaction { get; init; }
    public AllergySeverity Severity { get; init; }
    public AllergyStatus Status { get; init; }
    public DateOnly? OnsetDate { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string? Notes { get; init; }
    public string? StatusReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>One medicine line of a signed (or signed-then-cancelled) Clinical e-prescription.</summary>
internal sealed record PrescriptionItemRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid PrescriptionId { get; init; }
    public string PrescriptionNumber { get; init; } = string.Empty;
    public Guid? EncounterId { get; init; }
    public Guid? VisitId { get; init; }
    public Guid? AdmissionId { get; init; }
    public Guid PractitionerId { get; init; }
    public bool IsInpatient { get; init; }
    public PrescriptionStatus Status { get; init; }
    public DateTimeOffset PrescribedAt { get; init; }
    public DateTimeOffset? SignedAt { get; init; }
    public DateTimeOffset? CancelledAt { get; init; }
    public string? CancelReason { get; init; }
    public int RefillsAllowed { get; init; }
    public int Sequence { get; init; }
    public string DrugName { get; init; } = string.Empty;
    public string? GenericName { get; init; }
    public string? Strength { get; init; }
    public string? DosageForm { get; init; }
    public string? Route { get; init; }
    public string Dose { get; init; } = string.Empty;
    public DoseFrequency Frequency { get; init; }
    public string? CustomFrequency { get; init; }
    public int DurationDays { get; init; }
    public decimal Quantity { get; init; }
    public string? Instructions { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The prescription's own audit timestamp (its status lives there).</summary>
    public DateTimeOffset? PrescriptionUpdatedAt { get; init; }
}

/// <summary>Released laboratory report of one accession, with the released results it contains.</summary>
internal sealed record LabReportRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid LabOrderId { get; init; }
    public Guid? VisitId { get; init; }
    public string ReportNumber { get; init; } = string.Empty;
    public LabReportStatus Status { get; init; }
    public bool IsPartial { get; init; }
    public DateTimeOffset OrderedAt { get; init; }

    /// <summary>Earliest collection of the accession's samples (the clinically relevant time), when collected.</summary>
    public DateTimeOffset? CollectedAt { get; init; }

    /// <summary>When the report was first released (its first tests).</summary>
    public DateTimeOffset? FirstReleasedAt { get; init; }

    /// <summary>Its latest release or amendment.</summary>
    public DateTimeOffset? ReleasedAt { get; init; }

    public IReadOnlyList<Guid> ResultIds { get; init; } = [];
    public IReadOnlyList<string> TestNames { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>A radiologist's addendum to a signed report, with the user who added it.</summary>
internal sealed record RadiologyAddendumRow(string Text, string? Reason, DateTimeOffset AddedAt, Guid? AddedBy = null);

/// <summary>Released radiology report with its imaging study.</summary>
internal sealed record RadiologyReportRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid ImagingStudyId { get; init; }
    public Guid? VisitId { get; init; }
    public string AccessionNumber { get; init; } = string.Empty;
    public string ServiceCode { get; init; } = string.Empty;
    public string StudyName { get; init; } = string.Empty;
    public Modality Modality { get; init; }
    public string? BodyPart { get; init; }
    public DateTimeOffset OrderedAt { get; init; }

    /// <summary>When the patient was on the table, when the radiographer recorded it.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? AcquiredAt { get; init; }

    /// <summary>
    /// The study instance UID PACS holds the images under, once the study is linked with PACS; null before, when it is
    /// only the UID the worklist proposed.
    /// </summary>
    public string? StudyInstanceUid { get; init; }

    public int? SeriesCount { get; init; }
    public int? InstanceCount { get; init; }

    /// <summary>The user who submitted the report for sign-off.</summary>
    public Guid? ReportedBy { get; init; }

    /// <summary>The user who signed it off, and when.</summary>
    public Guid? VerifiedBy { get; init; }

    public DateTimeOffset? VerifiedAt { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public string? Technique { get; init; }
    public string? ClinicalHistory { get; init; }
    public string? Comparison { get; init; }
    public string Findings { get; init; } = string.Empty;
    public string Impression { get; init; } = string.Empty;
    public bool IsCriticalFinding { get; init; }
    public IReadOnlyList<RadiologyAddendumRow> Addenda { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Released cardiology report with its study.</summary>
internal sealed record CardiologyReportRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid? VisitId { get; init; }
    public string AccessionNumber { get; init; } = string.Empty;
    public string ServiceCode { get; init; } = string.Empty;
    public string StudyName { get; init; } = string.Empty;
    public CardiologyStudyType StudyType { get; init; }
    public DateTimeOffset OrderedAt { get; init; }
    public DateTimeOffset? AcquiredAt { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }
    public EcgFindings Ecg { get; init; } = new();
    public EchoFindings Echo { get; init; } = new();
    public TmtFindings Tmt { get; init; } = new();
    public HolterFindings Holter { get; init; } = new();
    public AbpmFindings Abpm { get; init; } = new();
    public string? Conclusion { get; init; }
    public string? Recommendations { get; init; }
    public bool IsCriticalFinding { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Document attached to the patient record, with the stored file's metadata.</summary>
internal sealed record PatientDocumentRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public PatientDocumentType Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public DateOnly? DocumentDate { get; init; }
    public string? Source { get; init; }
    public string? ContentType { get; init; }
    public long? SizeBytes { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Signed (or amended) discharge summary of an admission.</summary>
internal sealed record DischargeSummaryRow
{
    public required Guid Id { get; init; }
    public required Guid PatientId { get; init; }
    public Guid FacilityId { get; init; }
    public Guid AdmissionId { get; init; }
    public Guid? EncounterId { get; init; }
    public Guid? PractitionerId { get; init; }
    public Guid? SignedByPractitionerId { get; init; }
    public DateTimeOffset? AdmittedAt { get; init; }
    public DateTimeOffset? DischargedAt { get; init; }
    public string? Diagnoses { get; init; }
    public IReadOnlyList<string> DiagnosisCodes { get; init; } = [];
    public string? PresentingComplaints { get; init; }
    public string? CourseInHospital { get; init; }
    public string? Procedures { get; init; }
    public string? InvestigationsSummary { get; init; }
    public string? ConditionAtDischarge { get; init; }
    public string? Medications { get; init; }
    public string? Advice { get; init; }
    public DateOnly? FollowUpDate { get; init; }
    public string? FollowUpInstructions { get; init; }
    public DischargeSummaryStatus Status { get; init; }
    public DateTimeOffset? SignedAt { get; init; }
    public DateTimeOffset? AmendedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

