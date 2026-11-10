using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>Name and ABDM Health Facility Registry id of a facility.</summary>
internal sealed record FacilityInfo(string Name, string? HfrId);

/// <summary>Name and ABDM Healthcare Professionals Registry id (HPR ID) of a doctor or other practitioner.</summary>
internal sealed record PractitionerInfo(string Name, string? HprId);

/// <summary>
/// Display names for the people, departments, facilities, wards and beds a page of resources refers to, loaded once per
/// request. A practitioner or facility becomes a reference to its Practitioner or Organization resource
/// (<see cref="Linked"/>), with its name and the HPR ID of a practitioner or the HFR id of a facility, which ABDM systems
/// recognise; a ward or bed, to its Location. It also maps admissions to the in-patient visit that represents them as an
/// Encounter.
/// </summary>
internal sealed class ReferenceDirectory
{
    public static readonly ReferenceDirectory Empty = new();

    public IReadOnlyDictionary<Guid, PractitionerInfo> Practitioners { get; init; } = new Dictionary<Guid, PractitionerInfo>();

    public IReadOnlyDictionary<Guid, string> Departments { get; init; } = new Dictionary<Guid, string>();

    public IReadOnlyDictionary<Guid, FacilityInfo> Facilities { get; init; } = new Dictionary<Guid, FacilityInfo>();

    /// <summary>Admission id → the in-patient visit id (the Encounter that represents the stay).</summary>
    public IReadOnlyDictionary<Guid, Guid> AdmissionVisits { get; init; } = new Dictionary<Guid, Guid>();

    /// <summary>Ward id → name, and bed id → label: the places of a stay (its Encounter's locations).</summary>
    public IReadOnlyDictionary<Guid, string> Wards { get; init; } = new Dictionary<Guid, string>();

    public IReadOnlyDictionary<Guid, string> Beds { get; init; } = new Dictionary<Guid, string>();

    /// <summary>
    /// Practitioner, Organization and Location references also carry a literal reference (<c>Practitioner/{id}</c>,
    /// <c>Organization/{id}</c>, <c>Location/{id}</c>), which resolves: the FHIR read API serves them (the loader links
    /// them), and ABDM documents include their practitioners and organizations. Off only where the referenced resource
    /// cannot be had.
    /// </summary>
    public bool Linked { get; init; }

    /// <summary>The same names, with literal references (<see cref="Linked"/>).</summary>
    public ReferenceDirectory WithLinks() => new()
    {
        Practitioners = Practitioners,
        Departments = Departments,
        Facilities = Facilities,
        AdmissionVisits = AdmissionVisits,
        Wards = Wards,
        Beds = Beds,
        Linked = true,
    };

    public R4.ResourceReference? Practitioner(Guid? id) =>
        id is { } key && Practitioners.TryGetValue(key, out var practitioner)
            ? new R4.ResourceReference
            {
                Reference = Linked ? $"Practitioner/{FhirIds.Of(key)}" : null,
                Type = "Practitioner",
                Display = practitioner.Name,
                Identifier = string.IsNullOrWhiteSpace(practitioner.HprId) ? null : new R4.Identifier(FhirSystems.Hpr, Hpr.Format(practitioner.HprId)),
            }
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

    /// <summary>A ward the patient stayed in, as a Location (<c>Location/ward-{id}</c>, by name).</summary>
    public R4.ResourceReference? Ward(Guid id) => Wards.TryGetValue(id, out var name) ? Place(FhirIds.Ward, id, name) : null;

    /// <summary>A bed the patient lay in, as a Location (<c>Location/bed-{id}</c>, by its label).</summary>
    public R4.ResourceReference? Bed(Guid id) => Beds.TryGetValue(id, out var label) ? Place(FhirIds.Bed, id, label) : null;

    /// <summary>The Encounter of an admission (its in-patient visit), when known.</summary>
    public R4.ResourceReference? AdmissionEncounter(Guid? admissionId) =>
        admissionId is { } key && AdmissionVisits.TryGetValue(key, out var visitId)
            ? FhirReferences.Encounter(FhirIds.Visit, visitId)
            : null;

    private R4.ResourceReference Place(string prefix, Guid id, string name) => new()
    {
        Reference = Linked ? $"{FhirResourceTypes.Location}/{FhirIds.Of(prefix, id)}" : null,
        Type = FhirResourceTypes.Location,
        Display = name,
    };
}
