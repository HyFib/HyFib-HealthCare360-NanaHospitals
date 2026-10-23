using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// Canonical URLs of the NRCeS "FHIR Implementation Guide for ABDM" (package <c>ndhm.in</c>, validated against 6.5.0)
/// profiles the exported documents claim in <c>meta.profile</c>.
/// </summary>
internal static class AbdmProfiles
{
    public const string Base = "https://nrces.in/ndhm/fhir/r4/StructureDefinition/";

    public const string DocumentBundle = Base + "DocumentBundle";
    public const string DischargeSummaryRecord = Base + "DischargeSummaryRecord";
    public const string DiagnosticReportRecord = Base + "DiagnosticReportRecord";
    public const string OpConsultRecord = Base + "OPConsultRecord";
    public const string PrescriptionRecord = Base + "PrescriptionRecord";

    public const string Patient = Base + "Patient";
    public const string Practitioner = Base + "Practitioner";
    public const string Organization = Base + "Organization";
    public const string Encounter = Base + "Encounter";
    public const string Condition = Base + "Condition";
    public const string AllergyIntolerance = Base + "AllergyIntolerance";
    public const string MedicationRequest = Base + "MedicationRequest";
    public const string Observation = Base + "Observation";
    public const string ObservationVitalSigns = Base + "ObservationVitalSigns";
    public const string ObservationBodyMeasurement = Base + "ObservationBodyMeasurement";
    public const string DiagnosticReportLab = Base + "DiagnosticReportLab";
    public const string DiagnosticReportImaging = Base + "DiagnosticReportImaging";
    public const string ImagingStudy = Base + "ImagingStudy";
    public const string DocumentReference = Base + "DocumentReference";
    public const string Binary = Base + "Binary";
}

/// <summary>Code systems the IG adds to the ones the FHIR facade already uses (<see cref="FhirSystems"/>).</summary>
internal static class AbdmSystems
{
    /// <summary>The IG's identifier types (ABHA, HPID and others); its value set also takes every v2-0203 code.</summary>
    public const string IdentifierType = "https://nrces.in/ndhm/fhir/r4/CodeSystem/ndhm-identifier-type-code";

    public const string OrganizationType = "http://terminology.hl7.org/CodeSystem/organization-type";
    public const string Confidentiality = "http://terminology.hl7.org/CodeSystem/v3-Confidentiality";
    public const string CompositionAttestationMode = "http://hl7.org/fhir/composition-attestation-mode";

    /// <summary>RFC 3986 URIs, for the document's <c>urn:uuid:</c> identifiers.</summary>
    public const string Uri = "urn:ietf:rfc:3986";

    /// <summary>DICOM's own codes (DCM), for an imaging study's acquisition modality.</summary>
    public const string Dicom = "http://dicom.nema.org/resources/ontology/DCM";

    /// <summary>DICOM unique ids, as FHIR identifies a study by its study instance UID (<c>urn:oid:2.25.…</c>).</summary>
    public const string DicomUid = "urn:dicom:uid";

    /// <summary>FHIR's extension saying why an element the profile requires has no value.</summary>
    public const string DataAbsentReason = "http://hl7.org/fhir/StructureDefinition/data-absent-reason";

    // HC360 identifier namespaces used only in the documents.
    public const string Facility = "urn:hc360:facility";
    public const string ReferralLab = "urn:hc360:referral-lab";
    public const string Practitioner = "urn:hc360:practitioner";
    public const string MedicalRegistration = "urn:hc360:medical-council-registration";
}

/// <summary>
/// The SNOMED CT codes the IG fixes for each document type and section (the profiles fix code, system and display, so
/// these are copied from them verbatim), the codes HC360 chose where a profile fixes only the system (each says why),
/// and the LOINC codes of the extra narrative sections HC360 appends where the IG's open slicing allows it.
/// </summary>
internal static class AbdmCodes
{
    public static readonly AbdmCode DischargeSummary = Snomed("373942005", "Discharge summary");
    public static readonly AbdmCode DiagnosticStudiesReport = Snomed("721981007", "Diagnostic studies report");
    public static readonly AbdmCode LaboratoryReport = Snomed("4241000179101", "Laboratory report");

    /// <summary>
    /// The section of a radiology report. DiagnosticReportRecord fixes only the section code's system, and the IG's own
    /// imaging examples use 4261000179100 "Computed tomography imaging report"; this is its parent, which the IG's value set
    /// of diagnostic report types takes as the root of all imaging reports, so it fits every modality (as "Laboratory
    /// report" fits every laboratory section).
    /// </summary>
    public static readonly AbdmCode ImagingReport = Snomed("4201000179104", "Imaging report");

    public static readonly AbdmCode ConsultationReport = Snomed("371530004", "Clinical consultation report");
    public static readonly AbdmCode PrescriptionRecord = Snomed("440545006", "Prescription record");

    // Discharge summary and OP consultation sections.
    public static readonly AbdmCode ChiefComplaints = Snomed("422843007", "Chief complaint section");
    public static readonly AbdmCode PhysicalExamination = Snomed("425044008", "Physical exam section");
    public static readonly AbdmCode Allergies = Snomed("722446000", "Allergy record");
    public static readonly AbdmCode PastMedicalHistory = Snomed("1003642006", "Past medical history section");
    public static readonly AbdmCode HistoryAndPhysical = Snomed("371529009", "History and physical report");
    public static readonly AbdmCode Investigations = Snomed("721981007", "Diagnostic studies report");
    public static readonly AbdmCode MedicationHistory = Snomed("1003606003", "Medication history section");
    public static readonly AbdmCode MedicationSummary = Snomed("721912009", "Medication summary document");
    public static readonly AbdmCode PastProcedures = Snomed("1003640003", "History of past procedure section");
    public static readonly AbdmCode CarePlan = Snomed("734163000", "Care plan");
    public static readonly AbdmCode FollowUp = Snomed("390906007", "Follow-up encounter");
    public static readonly AbdmCode Referral = Snomed("306206005", "Referral to service");

    // Extra narrative sections (LOINC section codes from C-CDA), after the IG's own.
    public static readonly AbdmCode HospitalCourse = new(FhirSystems.Loinc, "8648-8", "Hospital course Narrative");
    public static readonly AbdmCode AssessmentAndPlan = new(FhirSystems.Loinc, "51847-2", "Evaluation + Plan note");

    // The IG fixes SNOMED CT as a report category's system, not its code.

    /// <summary>Diagnostic service section of a laboratory report.</summary>
    public static readonly AbdmCode PathologyService = Snomed("394595002", "Pathology");

    /// <summary>Diagnostic service section of a radiology report, the radiology department as Pathology is the laboratory.</summary>
    public static readonly AbdmCode RadiologyService = Snomed("394914008", "Radiology");

    private static AbdmCode Snomed(string code, string display) => new(FhirSystems.Snomed, code, display);
}

/// <summary>A code with its system and the display the IG requires.</summary>
internal sealed record AbdmCode(string System, string Code, string Display)
{
    public R4.Coding Coding() => new(System, Code, Display);

    public R4.CodeableConcept Concept(string? text = null) => new() { Coding = [Coding()], Text = text };
}
