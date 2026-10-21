using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>Name and ABDM Health Facility Registry id of a facility.</summary>
internal sealed record FacilityInfo(string Name, string? HfrId);

/// <summary>
/// Display names for the people, departments and facilities a page of resources refers to, loaded once per request.
/// The facade does not serve Practitioner or Organization resources, so these become display references (with the HFR
/// id for facilities, which ABDM systems recognise); an ABDM document, which includes them, links them (<see cref="Linked"/>).
/// It also maps admissions to the in-patient visit that represents them as an Encounter.
/// </summary>
internal sealed class ReferenceDirectory
{
    public static readonly ReferenceDirectory Empty = new();

    public IReadOnlyDictionary<Guid, string> Practitioners { get; init; } = new Dictionary<Guid, string>();

    public IReadOnlyDictionary<Guid, string> Departments { get; init; } = new Dictionary<Guid, string>();

    public IReadOnlyDictionary<Guid, FacilityInfo> Facilities { get; init; } = new Dictionary<Guid, FacilityInfo>();

    /// <summary>Admission id → the in-patient visit id (the Encounter that represents the stay).</summary>
    public IReadOnlyDictionary<Guid, Guid> AdmissionVisits { get; init; } = new Dictionary<Guid, Guid>();

    /// <summary>
    /// Practitioner and Organization references also carry a literal reference (<c>Practitioner/{id}</c>,
    /// <c>Organization/{id}</c>): set for ABDM documents, which include those resources and resolve every reference.
    /// The FHIR read API, which does not serve them, leaves it off.
    /// </summary>
    public bool Linked { get; init; }

    /// <summary>The same names, with literal Practitioner and Organization references (<see cref="Linked"/>).</summary>
    public ReferenceDirectory WithLinks() => new()
    {
        Practitioners = Practitioners,
        Departments = Departments,
        Facilities = Facilities,
        AdmissionVisits = AdmissionVisits,
        Linked = true,
    };

    public R4.ResourceReference? Practitioner(Guid? id) =>
        id is { } key && Practitioners.TryGetValue(key, out var name)
            ? new R4.ResourceReference { Reference = Linked ? $"Practitioner/{FhirIds.Of(key)}" : null, Type = "Practitioner", Display = name }
            : null;

    public string? Department(Guid? id) => id is { } key && Departments.TryGetValue(key, out var name) ? name : null;

    public R4.ResourceReference? Organization(Guid? facilityId)
    {
        if (facilityId is not { } key || !Facilities.TryGetValue(key, out var facility))
        {
            return null;
        }

        return new R4.ResourceReference
        {
            Reference = Linked ? $"Organization/{FhirIds.Of(key)}" : null,
            Type = "Organization",
            Display = facility.Name,
            Identifier = string.IsNullOrWhiteSpace(facility.HfrId) ? null : new R4.Identifier(FhirSystems.Hfr, facility.HfrId.Trim()),
        };
    }

    /// <summary>The Encounter of an admission (its in-patient visit), when known.</summary>
    public R4.ResourceReference? AdmissionEncounter(Guid? admissionId) =>
        admissionId is { } key && AdmissionVisits.TryGetValue(key, out var visitId)
            ? FhirReferences.Encounter(FhirIds.Visit, visitId)
            : null;
}
