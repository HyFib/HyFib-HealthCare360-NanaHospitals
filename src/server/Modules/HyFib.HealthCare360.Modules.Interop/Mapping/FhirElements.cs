using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>References between the facade's resources.</summary>
internal static class FhirReferences
{
    public static R4.ResourceReference Patient(Guid patientId) => new($"Patient/{FhirIds.Of(patientId)}");

    public static R4.ResourceReference Encounter(string prefix, Guid id) => new($"Encounter/{FhirIds.Of(prefix, id)}");

    public static R4.ResourceReference Observation(string id) => new($"Observation/{id}");

    /// <summary>
    /// The Encounter a clinical record belongs to: the clinician's encounter when known, otherwise the visit, otherwise
    /// the in-patient visit of the admission.
    /// </summary>
    public static R4.ResourceReference? Context(Guid? encounterId, Guid? visitId, Guid? admissionId, ReferenceDirectory refs) =>
        encounterId is { } encounter ? Encounter(FhirIds.ClinicalEncounter, encounter)
        : visitId is { } visit ? Encounter(FhirIds.Visit, visit)
        : refs.AdmissionEncounter(admissionId);
}

/// <summary>Small builders shared by the mappers.</summary>
internal static class FhirElements
{
    /// <summary><c>meta.lastUpdated</c> from the audit columns (last update, else creation), optionally with profiles.</summary>
    public static R4.Meta Meta(DateTimeOffset createdAt, DateTimeOffset? updatedAt, params string[] profiles)
    {
        var meta = new R4.Meta { LastUpdated = FhirTime.ToMilliseconds(Latest(createdAt, updatedAt)) };
        if (profiles.Length > 0)
        {
            meta.Profile = profiles;
        }

        return meta;
    }

    public static DateTimeOffset Latest(DateTimeOffset createdAt, params DateTimeOffset?[] updates)
    {
        var latest = createdAt;
        foreach (var update in updates)
        {
            if (update is { } value && value > latest)
            {
                latest = value;
            }
        }

        return latest;
    }

    public static R4.Coding Coding(string system, string code, string? display = null) => new(system, code, display);

    public static R4.CodeableConcept Concept(string system, string code, string? display = null, string? text = null) =>
        new() { Coding = [Coding(system, code, display)], Text = text };

    public static R4.CodeableConcept Text(string text) => new() { Text = text };

    public static R4.Identifier Identifier(string system, string value, string? typeCode = null, string? typeDisplay = null) =>
        new(system, value)
        {
            Type = typeCode is null ? null : Concept(FhirSystems.IdentifierType, typeCode, typeDisplay),
        };

    public static R4.Annotation? Note(string? text) => Clean(text) is { } value ? new R4.Annotation { Text = value } : null;

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Drops trailing zeros (numeric(18,2) columns return 172.00; FHIR decimals keep their precision).</summary>
    public static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;

    /// <summary>A UCUM quantity (value normalised).</summary>
    public static R4.Quantity Ucum(decimal value, string code, string unit) =>
        new() { Value = Normalize(value), Unit = unit, System = FhirSystems.Ucum, Code = code };
}
