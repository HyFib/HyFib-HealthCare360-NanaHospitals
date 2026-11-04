namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>The FHIR R4 resource types served by the facade (EHR-05).</summary>
internal static class FhirResourceTypes
{
    public const string Patient = "Patient";
    public const string Encounter = "Encounter";
    public const string Observation = "Observation";
    public const string Condition = "Condition";
    public const string AllergyIntolerance = "AllergyIntolerance";
    public const string MedicationRequest = "MedicationRequest";
    public const string DiagnosticReport = "DiagnosticReport";
    public const string DocumentReference = "DocumentReference";
    public const string Immunization = "Immunization";
    public const string Procedure = "Procedure";
    public const string Practitioner = "Practitioner";
    public const string Organization = "Organization";
    public const string Location = "Location";

    public static IReadOnlyList<string> Names { get; } =
    [
        Patient, Encounter, Observation, Condition, AllergyIntolerance, MedicationRequest, DiagnosticReport, DocumentReference, Immunization, Procedure,
        Practitioner, Organization, Location,
    ];

    /// <summary>
    /// The hospital's directory: its practitioners, its facilities and the places of care in them (wards, rooms and beds).
    /// They are in no patient's record, so they are read without a patient's consent, and what the records say of the
    /// people and places in them can be looked up.
    /// </summary>
    public static IReadOnlyList<string> Directory { get; } = [Practitioner, Organization, Location];

    /// <summary>Resource type names are case-sensitive in FHIR.</summary>
    public static bool IsServed(string? type) => type is not null && Names.Contains(type, StringComparer.Ordinal);

    public static bool IsDirectory(string? type) => type is not null && Directory.Contains(type, StringComparer.Ordinal);
}
