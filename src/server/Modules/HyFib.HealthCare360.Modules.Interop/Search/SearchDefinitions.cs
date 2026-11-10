using System.Globalization;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Search;

/// <summary>A supported search parameter of a resource type (also published in the CapabilityStatement).</summary>
internal sealed record SearchParameterDefinition(string Name, R4.SearchParamType Type, string Documentation);

/// <summary>
/// What a resource type supports. <paramref name="StatusParameter"/> and <paramref name="IsEnteredInError"/> describe
/// records recorded by mistake: searches leave them out unless the client asks for that status explicitly (reads by id
/// always return them, so receivers can retract data they synchronised earlier). A search of a type with
/// <paramref name="Selectors"/> needs one of them, so the API cannot be used to list every one.
/// </summary>
internal sealed record ResourceSearchDefinition(
    string Type,
    string Documentation,
    IReadOnlyList<SearchParameterDefinition> Parameters,
    string? StatusParameter = null,
    Func<R4.Resource, bool>? IsEnteredInError = null,
    IReadOnlyList<string>? Selectors = null)
{
    /// <summary>The types of a patient's record are searched within one patient's record; Patient and the hospital's directory are not.</summary>
    public bool IsPatientScoped => Type != FhirResourceTypes.Patient && !FhirResourceTypes.IsDirectory(Type);

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
    public const string Sort = "_sort";

    /// <summary>Page size when <c>_count</c> is not given.</summary>
    public const int DefaultCount = 20;

    /// <summary>Largest page; a larger <c>_count</c> is capped (the self link shows the size used).</summary>
    public const int MaxCount = 100;

    /// <summary>Patient searches need at least one of these, so the API cannot be used to list every patient.</summary>
    public static readonly string[] PatientSelectors = [Id, "identifier", "name", "birthdate", LastUpdated];

    /// <summary>Practitioner searches need at least one of these: a practitioner is looked up, the staff are not listed.</summary>
    public static readonly string[] PractitionerSelectors = [Id, "identifier", "name"];

    /// <summary>
    /// Why a parameter cannot order results (<c>_sort</c>), or null when it can: dates, strings and codes order, a reference
    /// has no order of its own, and identifiers of different systems have none between them.
    /// </summary>
    public static string? WhyNotSortable(SearchParameterDefinition parameter) => parameter.Type switch
    {
        R4.SearchParamType.Date or R4.SearchParamType.String => null,
        R4.SearchParamType.Token when parameter.Name != "identifier" => null,
        R4.SearchParamType.Token => "identifiers of different systems have no order between them",
        R4.SearchParamType.Reference => "a reference has no order of its own",
        _ => "its values have no order",
    };

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
            ],
            Selectors: PatientSelectors),
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
            string.Create(CultureInfo.InvariantCulture, $"Vital signs (vs-{{id}}-{{measurement}}), released laboratory results (lab-{{id}}) and readings of home-monitoring devices (rpm-{{id}}; a search reaching more than {DeviceReadingObservations.MaxPerSearch:N0} of them is refused as too costly: narrow its date range)."),
            [
                PatientParameter, SubjectParameter, IdParameter,
                new("category", R4.SearchParamType.Token, "vital-signs | laboratory | survey | exam | procedure (an ECG patch's rhythm)."),
                new("code", R4.SearchParamType.Token, "LOINC code, for example http://loinc.org|8867-4, or an HC360 test code (urn:hc360:lab-test)."),
                new("date", R4.SearchParamType.Date, "effectiveDateTime (recording, sample collection or measurement time)."),
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
            "Documents attached to the record (doc-{id}; metadata only), signed discharge summaries (ds-{id}; full text) and signed "
            + "clinical notes (note-{id}; full text, an addendum relatesTo the note it appends to).",
            [
                PatientParameter, SubjectParameter, IdParameter,
                new("type", R4.SearchParamType.Token,
                    "LOINC document type, for example http://loinc.org|18842-5 (discharge summary) or http://loinc.org|11506-3 (progress note)."),
                new("date", R4.SearchParamType.Date, "When the document reference was created, or the summary or note signed."),
                new("status", R4.SearchParamType.Token, "current."),
                LastUpdatedParameter,
            ],
            "status",
            resource => resource is R4.DocumentReference { Status: R4.DocumentReferenceStatus.EnteredInError }),
        new(FhirResourceTypes.Immunization,
            "Vaccine doses given here or copied from the patient's card (primarySource false).",
            [
                PatientParameter, IdParameter,
                new("status", R4.SearchParamType.Token, "completed | entered-in-error (such records are left out unless asked for)."),
                new("vaccine-code", R4.SearchParamType.Token, "SNOMED CT vaccine product, for example http://snomed.info/sct|1861000221106 (BCG)."),
                new("date", R4.SearchParamType.Date, "The day the dose was given."),
                LastUpdatedParameter,
            ],
            "status",
            resource => resource is R4.Immunization { Status: R4.Immunization.ImmunizationStatusCodes.EnteredInError }),
        new(FhirResourceTypes.Procedure,
            "Procedures recorded on the chart, and operations recorded when the theatre completed them (category 387713003 Surgical procedure). The procedure is named, not coded: the ABDM guide fixes its code system to SNOMED CT, and HC360 codes procedures in ICD-10-PCS.",
            [
                PatientParameter, IdParameter,
                new("status", R4.SearchParamType.Token, "completed | entered-in-error (such records are left out unless asked for)."),
                new("date", R4.SearchParamType.Date, "When the procedure was performed."),
                LastUpdatedParameter,
            ],
            "status",
            resource => resource is R4.Procedure { Status: R4.EventStatus.EnteredInError }),
        new(FhirResourceTypes.Practitioner,
            "The hospital's practitioners, as its ABDM documents carry them (NRCeS Practitioner profile): name, gender, qualification, HPR ID, council registration and staff code. Never a phone number or e-mail. No consent is needed: nothing here is a patient's. A search needs _id, identifier or name.",
            [
                IdParameter,
                new("identifier", R4.SearchParamType.Token, "HPR ID (https://doctor.ndhm.gov.in|21-1521-3828-3227), council registration (urn:hc360:medical-council-registration) or staff code (urn:hc360:practitioner); a value without a system matches any."),
                new("name", R4.SearchParamType.String, "Words of the name: each must start a word of it (case-insensitive)."),
                new("active", R4.SearchParamType.Token, "true | false: in use, or taken out of use."),
                LastUpdatedParameter,
            ],
            Selectors: PractitionerSelectors),
        new(FhirResourceTypes.Organization,
            "The hospital's facilities (NRCeS Organization profile): name, HFR ID, facility code, and the facility's own phone, e-mail and address. No consent is needed: nothing here is a patient's.",
            [
                IdParameter,
                new("identifier", R4.SearchParamType.Token, "HFR ID (https://facility.ndhm.gov.in|IN2710000123) or facility code (urn:hc360:facility); a value without a system matches either."),
                new("name", R4.SearchParamType.String, "Words of the name: each must start a word of it (case-insensitive)."),
                new("active", R4.SearchParamType.Token, "true | false: in use, or taken out of use."),
                LastUpdatedParameter,
            ]),
        new(FhirResourceTypes.Location,
            "The places of care of the reader's facility: its wards (ward-{id}), the rooms in them (room-{id}) and their beds (bed-{id}), base FHIR R4 (the ABDM guide has no Location profile). An in-patient Encounter names the ward and the bed of each part of the stay. No consent is needed: nothing here is a patient's.",
            [
                IdParameter,
                new("identifier", R4.SearchParamType.Token, "Ward code (urn:hc360:ward) or bed label (urn:hc360:bed); a value without a system matches either."),
                new("name", R4.SearchParamType.String, "Words of the name: each must start a word of it (case-insensitive)."),
                new("type", R4.SearchParamType.Token, "Kind of ward (v3 RoleCode): HU hospital unit, ICU, PEDICU, PEDNICU or ER; a room or bed has its ward's."),
                new("partof", R4.SearchParamType.Reference, "The ward or room a place is in: Location/ward-{id} or Location/room-{id}."),
                new("organization", R4.SearchParamType.Reference, "The facility: Organization/{id}."),
                new("status", R4.SearchParamType.Token, "active | inactive: in use, or taken out of use."),
                LastUpdatedParameter,
            ]),
    ];

    public static ResourceSearchDefinition? Find(string? type) => All.FirstOrDefault(d => string.Equals(d.Type, type, StringComparison.Ordinal));
}
