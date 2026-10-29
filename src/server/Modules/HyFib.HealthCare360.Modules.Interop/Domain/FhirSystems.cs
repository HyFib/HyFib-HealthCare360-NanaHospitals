namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>
/// Code systems and identifier systems used by the facade: international terminologies (LOINC, ICD-10, SNOMED CT,
/// UCUM), HL7 terminology code systems, ABDM registries (ABHA, HFR, HPR) and HC360's own identifier namespaces.
/// </summary>
internal static class FhirSystems
{
    // International terminologies.
    public const string Loinc = "http://loinc.org";
    public const string Icd10 = "http://hl7.org/fhir/sid/icd-10";

    /// <summary>WHO ICD-11 for Mortality and Morbidity Statistics, as HL7 terminology names it.</summary>
    public const string Icd11Mms = "http://id.who.int/icd/release/11/mms";
    public const string Snomed = "http://snomed.info/sct";
    public const string Ucum = "http://unitsofmeasure.org";
    public const string Language = "urn:ietf:bcp:47";

    // HL7 terminology (THO) code systems.
    public const string ObservationCategory = "http://terminology.hl7.org/CodeSystem/observation-category";
    public const string ObservationInterpretation = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation";
    public const string DataAbsentReason = "http://terminology.hl7.org/CodeSystem/data-absent-reason";
    public const string ConditionClinical = "http://terminology.hl7.org/CodeSystem/condition-clinical";
    public const string ConditionVerification = "http://terminology.hl7.org/CodeSystem/condition-ver-status";
    public const string ConditionCategory = "http://terminology.hl7.org/CodeSystem/condition-category";
    public const string AllergyClinical = "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical";
    public const string AllergyVerification = "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification";
    public const string ActCode = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
    public const string ParticipationType = "http://terminology.hl7.org/CodeSystem/v3-ParticipationType";
    public const string AdmitSource = "http://terminology.hl7.org/CodeSystem/admit-source";
    public const string DischargeDisposition = "http://terminology.hl7.org/CodeSystem/discharge-disposition";
    public const string IdentifierType = "http://terminology.hl7.org/CodeSystem/v2-0203";
    public const string DiagnosticServiceSection = "http://terminology.hl7.org/CodeSystem/v2-0074";
    public const string MaritalStatus = "http://terminology.hl7.org/CodeSystem/v3-MaritalStatus";
    public const string NullFlavor = "http://terminology.hl7.org/CodeSystem/v3-NullFlavor";
    public const string ContactRole = "http://terminology.hl7.org/CodeSystem/v2-0131";
    public const string RoleCode = "http://terminology.hl7.org/CodeSystem/v3-RoleCode";
    public const string MedicationRequestCategory = "http://terminology.hl7.org/CodeSystem/medicationrequest-category";
    public const string TimingAbbreviation = "http://terminology.hl7.org/CodeSystem/v3-GTSAbbreviation";

    // FHIR-defined code systems of coded elements (used when a client searches with a system-qualified token).
    public const string AdministrativeGender = "http://hl7.org/fhir/administrative-gender";
    public const string ObservationStatus = "http://hl7.org/fhir/observation-status";
    public const string EncounterStatus = "http://hl7.org/fhir/encounter-status";
    public const string MedicationRequestStatus = "http://hl7.org/fhir/CodeSystem/medicationrequest-status";
    public const string MedicationRequestIntent = "http://hl7.org/fhir/CodeSystem/medicationrequest-intent";
    public const string DiagnosticReportStatus = "http://hl7.org/fhir/diagnostic-report-status";
    public const string DocumentReferenceStatus = "http://hl7.org/fhir/document-reference-status";
    public const string AllergyCategory = "http://hl7.org/fhir/allergy-intolerance-category";

    // Profiles.
    public const string VitalSignsProfile = "http://hl7.org/fhir/StructureDefinition/";

    // ABDM registries: ABHA number (Ayushman Bharat Health Account), Health Facility Registry and Healthcare Professionals
    // Registry (the system the NRCeS guide's examples give a doctor's HPR ID).
    public const string Abha = "https://healthid.ndhm.gov.in";
    public const string Hfr = "https://facility.ndhm.gov.in";
    public const string Hpr = "https://doctor.ndhm.gov.in";

    // HC360 identifier namespaces.
    public const string Uhid = "urn:hc360:uhid";
    public const string VisitNumber = "urn:hc360:visit-number";
    public const string AdmissionNumber = "urn:hc360:admission-number";
    public const string PrescriptionNumber = "urn:hc360:prescription-number";
    public const string AccessionNumber = "urn:hc360:accession-number";
    public const string LabTest = "urn:hc360:lab-test";
    public const string Service = "urn:hc360:service";

    /// <summary>Machine-readable reasons in <c>OperationOutcome.issue.details</c>.</summary>
    public const string Issue = "urn:hc360:fhir-issue";
}
