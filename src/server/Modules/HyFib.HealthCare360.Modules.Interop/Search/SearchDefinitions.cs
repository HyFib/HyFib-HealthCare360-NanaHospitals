using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Search;

/// <summary>A supported search parameter of a resource type (also published in the CapabilityStatement).</summary>
internal sealed record SearchParameterDefinition(string Name, R4.SearchParamType Type, string Documentation);

/// <summary>
/// What a resource type supports. <paramref name="StatusParameter"/> and <paramref name="IsEnteredInError"/> describe
/// records recorded by mistake: searches leave them out unless the client asks for that status explicitly (reads by id
/// always return them, so receivers can retract data they synchronised earlier).
/// </summary>
internal sealed record ResourceSearchDefinition(
    string Type,
    string Documentation,
    IReadOnlyList<SearchParameterDefinition> Parameters,
    string? StatusParameter = null,
    Func<R4.Resource, bool>? IsEnteredInError = null)
{
    /// <summary>Every type except Patient is searched within one patient's record.</summary>
    public bool IsPatientScoped => Type != FhirResourceTypes.Patient;

    public SearchParameterDefinition? Find(string name) => Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}

/// <summary>The single source of truth for search: the parser, the in-memory matcher and the CapabilityStatement.</summary>
internal static class SearchDefinitions
{
    public const string Patient = "patient";
    public const string Subject = "subject";
    public const string Count = "_count";
    public const string Offset = "_offset";
    public const string Format = "_format";
    public const string Pretty = "_pretty";
    public const string Id = "_id";
    public const string LastUpdated = "_lastUpdated";

    /// <summary>Page size when <c>_count</c> is not given.</summary>
    public const int DefaultCount = 20;

    /// <summary>Largest page; a larger <c>_count</c> is capped (the self link shows the size used).</summary>
    public const int MaxCount = 100;

    /// <summary>Patient searches need at least one of these, so the API cannot be used to list every patient.</summary>
    public static readonly string[] PatientSelectors = [Id, "identifier", "name", "birthdate", LastUpdated];

    private static readonly SearchParameterDefinition IdParameter = new(Id, R4.SearchParamType.Token, "Logical id (comma-separated for several).");

    private static readonly SearchParameterDefinition LastUpdatedParameter = new(LastUpdated, R4.SearchParamType.Date,
        "meta.lastUpdated (audit timestamp) with eq, ne, gt, ge, lt or le; date values are IST.");

    private static readonly SearchParameterDefinition PatientParameter = new(Patient, R4.SearchParamType.Reference,
        "Required. The patient: Patient/{id} or {id}. One patient per search.");

    private static readonly SearchParameterDefinition SubjectParameter = new(Subject, R4.SearchParamType.Reference,
        "Alternative to patient (Patient/{id}).");

    public static IReadOnlyList<ResourceSearchDefinition> All { get; } =
    [
        new(FhirResourceTypes.Patient,
            "Patients with an active record-sharing consent. Identifiers: UHID (urn:hc360:uhid) and ABHA number (https://healthid.ndhm.gov.in).",
            [
                IdParameter,
                new("identifier", R4.SearchParamType.Token, "UHID (urn:hc360:uhid|NH-UH-2600123) or ABHA number (https://healthid.ndhm.gov.in|12-3456-7890-1234); a value without a system matches either."),
                new("name", R4.SearchParamType.String, "Words of the name (each word must appear, case-insensitive; at least 2 characters)."),
                new("birthdate", R4.SearchParamType.Date, "Date of birth: yyyy, yyyy-MM or yyyy-MM-dd with a prefix. Estimated dates of birth are published with the year only."),
                new("gender", R4.SearchParamType.Token, "male | female | other | unknown."),
                LastUpdatedParameter,
            ]),
        new(FhirResourceTypes.Encounter,
            "Front-office visits (visit-{id}; in-patient visits carry the admission) and clinicians' encounters (enc-{id}, partOf the visit).",
            [
                PatientParameter, SubjectParameter, IdParameter,
                new("date", R4.SearchParamType.Date, "Encounter period (an ongoing stay has no end)."),
                new("status", R4.SearchParamType.Token, "arrived | in-progress | finished | cancelled."),
                new("class", R4.SearchParamType.Token, "v3 ActCode: AMB | IMP | EMER | SS | VR."),
                LastUpdatedParameter,
            ]),
        new(FhirResourceTypes.Observation,
            "Vital signs (vs-{id}-{measurement}) and released laboratory results (lab-{id}).",
            [
                PatientParameter, SubjectParameter, IdParameter,
                new("category", R4.SearchParamType.Token, "vital-signs | laboratory | survey | exam."),
                new("code", R4.SearchParamType.Token, "LOINC code, for example http://loinc.org|8867-4, or an HC360 test code (urn:hc360:lab-test)."),
                new("date", R4.SearchParamType.Date, "effectiveDateTime (recording or sample collection time)."),
                new("status", R4.SearchParamType.Token, "final | amended | entered-in-error (entered-in-error is left out unless asked for)."),
                LastUpdatedParameter,
            ],
            "status",
            resource => resource is R4.Observation { Status: R4.ObservationStatus.EnteredInError }),
        new(FhirResourceTypes.Condition,
            "Diagnoses and the problem list, coded with ICD-10 (http://hl7.org/fhir/sid/icd-10) or ICD-11 MMS (http://id.who.int/icd/release/11/mms).",
            [
                PatientParameter, SubjectParameter, IdParameter,
                new("clinical-status", R4.SearchParamType.Token, "active | resolved."),
                new("verification-status", R4.SearchParamType.Token, "provisional | confirmed | differential."),
                new("category", R4.SearchParamType.Token, "problem-list-item | encounter-diagnosis."),
                new("code", R4.SearchParamType.Token, "ICD-10 or ICD-11 code, for example http://hl7.org/fhir/sid/icd-10|E11.9 or http://id.who.int/icd/release/11/mms|5A11."),
                new("onset-date", R4.SearchParamType.Date, "onsetDateTime."),
                new("recorded-date", R4.SearchParamType.Date, "recordedDate."),
                LastUpdatedParameter,
            ]),
        new(FhirResourceTypes.AllergyIntolerance,
            "Allergies and intolerances.",
            [
                PatientParameter, IdParameter,
                new("clinical-status", R4.SearchParamType.Token, "active | inactive | resolved."),
                new("verification-status", R4.SearchParamType.Token, "entered-in-error (such records are left out unless asked for)."),
                new("category", R4.SearchParamType.Token, "medication | food | environment."),
                new("date", R4.SearchParamType.Date, "recordedDate."),
                LastUpdatedParameter,
            ],
            "verification-status",
            resource => resource is R4.AllergyIntolerance { VerificationStatus: { } status } && status.Coding.Any(c => c.Code == "entered-in-error")),
        new(FhirResourceTypes.MedicationRequest,
            "Signed e-prescription lines (one per medicine; groupIdentifier = prescription number).",
            [
                PatientParameter, SubjectParameter, IdParameter,
                new("status", R4.SearchParamType.Token, "active | completed | cancelled."),
                new("intent", R4.SearchParamType.Token, "order."),
                new("category", R4.SearchParamType.Token, "inpatient | outpatient."),
                new("authoredon", R4.SearchParamType.Date, "When the prescription was signed."),
                LastUpdatedParameter,
            ]),
        new(FhirResourceTypes.DiagnosticReport,
            "Released laboratory (lab-{id}), radiology (rad-{id}) and cardiology (card-{id}) reports.",
            [
                PatientParameter, SubjectParameter, IdParameter,
                new("category", R4.SearchParamType.Token, "v2-0074 section: LAB | RAD | EC | CUS | OTH."),
                new("code", R4.SearchParamType.Token, "LOINC report code (11502-2, 18748-4, 11524-6) or an HC360 service code (urn:hc360:service)."),
                new("date", R4.SearchParamType.Date, "effectiveDateTime (sample collection or acquisition)."),
                new("status", R4.SearchParamType.Token, "final | partial | amended | appended."),
                LastUpdatedParameter,
            ]),
        new(FhirResourceTypes.DocumentReference,
            "Documents attached to the record (doc-{id}; metadata only) and signed discharge summaries (ds-{id}; full text).",
            [
                PatientParameter, SubjectParameter, IdParameter,
                new("type", R4.SearchParamType.Token, "LOINC document type, for example http://loinc.org|18842-5 (discharge summary)."),
                new("date", R4.SearchParamType.Date, "When the document reference was created or the summary signed."),
                new("status", R4.SearchParamType.Token, "current."),
                LastUpdatedParameter,
            ],
            "status",
            resource => resource is R4.DocumentReference { Status: R4.DocumentReferenceStatus.EnteredInError }),
    ];

    public static ResourceSearchDefinition? Find(string? type) => All.FirstOrDefault(d => string.Equals(d.Type, type, StringComparison.Ordinal));
}
