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

    public static IReadOnlyList<string> Names { get; } =
        [Patient, Encounter, Observation, Condition, AllergyIntolerance, MedicationRequest, DiagnosticReport, DocumentReference];

    /// <summary>Resource type names are case-sensitive in FHIR.</summary>
    public static bool IsServed(string? type) => type is not null && Names.Contains(type, StringComparer.Ordinal);
}
