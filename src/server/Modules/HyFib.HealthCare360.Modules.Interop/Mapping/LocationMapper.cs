using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>Inpatient module <see cref="Inpatient.Domain.Ward"/>: a nursing unit of a facility.</summary>
internal sealed record WardRow
{
    public required Guid Id { get; init; }
    public Guid FacilityId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public WardType Type { get; init; }
    public bool IsActive { get; init; } = true;
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Inpatient module <see cref="Inpatient.Domain.Room"/>: a room inside a ward.</summary>
internal sealed record RoomRow
{
    public required Guid Id { get; init; }
    public Guid FacilityId { get; init; }
    public Guid WardId { get; init; }
    public string Number { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Inpatient module <see cref="Inpatient.Domain.Bed"/>: a bed in a ward, and in a room of it when the ward has rooms.</summary>
internal sealed record BedRow
{
    public required Guid Id { get; init; }
    public Guid FacilityId { get; init; }
    public Guid WardId { get; init; }
    public Guid? RoomId { get; init; }
    public string Label { get; init; } = string.Empty;
    public bool IsActive { get; init; } = true;
    public bool IsDeleted { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>One continuous stay in one bed (Inpatient <see cref="BedAssignment"/>): the bed, its ward then, and when.</summary>
internal sealed record BedStayRow(Guid BedId, Guid WardId, DateTimeOffset From, DateTimeOffset? To);

/// <summary>
/// The places of care as FHIR Locations, base R4 (the ABDM guide, 6.5.0, has no Location profile): a ward, the rooms in
/// it and its beds, each <c>partOf</c> the one above it and managed by its facility. A Location says what and where a
/// place is, never what happens in it: no free text (a ward's notes, a room's description, why a bed is blocked, which
/// may name a patient) and no occupancy.
/// </summary>
internal static class LocationMapper
{
    public static R4.Location Ward(WardRow ward)
    {
        var location = Place(FhirIds.Of(FhirIds.Ward, ward.Id), ward.Name, "wa", "Ward", ward.Type, ward.FacilityId, ward.IsActive, ward.CreatedAt, ward.UpdatedAt);
        location.Identifier.Add(new R4.Identifier(FhirSystems.Ward, ward.Code) { Use = R4.Identifier.IdentifierUse.Usual });
        return location;
    }

    public static R4.Location Room(RoomRow room, WardRow ward)
    {
        var location = Place(
            FhirIds.Of(FhirIds.Room, room.Id), RoomName(room, ward), "ro", "Room", ward.Type, room.FacilityId, room.IsActive, room.CreatedAt,
            Latest(room.UpdatedAt, ward.UpdatedAt));
        location.PartOf = Reference(FhirIds.Of(FhirIds.Ward, ward.Id), ward.Name);
        return location;
    }

    /// <summary>A bed is part of its room, or of its ward when the ward has no rooms; it does the work of its ward.</summary>
    public static R4.Location Bed(BedRow bed, WardRow ward, RoomRow? room)
    {
        var location = Place(
            FhirIds.Of(FhirIds.Bed, bed.Id), bed.Label, "bd", "Bed", ward.Type, bed.FacilityId, bed.IsActive, bed.CreatedAt,
            Latest(bed.UpdatedAt, ward.UpdatedAt, room?.UpdatedAt));
        location.Identifier.Add(new R4.Identifier(FhirSystems.Bed, bed.Label) { Use = R4.Identifier.IdentifierUse.Usual });
        location.PartOf = room is null
            ? Reference(FhirIds.Of(FhirIds.Ward, ward.Id), ward.Name)
            : Reference(FhirIds.Of(FhirIds.Room, room.Id), RoomName(room, ward));
        return location;
    }

    /// <summary>
    /// The kind of ward as a v3 service delivery location type (the binding is extensible): the most specific code the
    /// value set has, with the ward type in words. Only intensive care, its paediatric and neonatal units and the emergency
    /// ward have a code of their own; every other ward is a hospital unit (HU).
    /// </summary>
    public static R4.CodeableConcept ServiceType(WardType type)
    {
        var (code, display) = type switch
        {
            WardType.Icu => ("ICU", "Intensive care unit"),
            WardType.Picu => ("PEDICU", "Pediatric intensive care unit"),
            WardType.Nicu => ("PEDNICU", "Pediatric neonatal intensive care unit"),
            WardType.Emergency => ("ER", "Emergency room"),
            _ => ("HU", "Hospital unit"),
        };

        return FhirElements.Concept(FhirSystems.RoleCode, code, display, ServiceTypeText(type));
    }

    public static string ServiceTypeText(WardType type) => type switch
    {
        WardType.General => "General ward",
        WardType.SemiPrivate => "Semi-private ward",
        WardType.Private => "Private ward",
        WardType.Deluxe => "Deluxe ward",
        WardType.Icu => "Intensive care unit",
        WardType.Nicu => "Neonatal intensive care unit",
        WardType.Picu => "Paediatric intensive care unit",
        WardType.Hdu => "High dependency unit",
        WardType.Maternity => "Maternity ward",
        WardType.Isolation => "Isolation ward",
        WardType.DayCare => "Day care ward",
        WardType.Emergency => "Emergency ward",
        _ => "Ward",
    };

    public static string RoomName(RoomRow room, WardRow ward) => $"{ward.Name}, room {room.Number}";

    /// <summary>
    /// When a room or bed last changed as served: its own row, or the ward (and room) it takes its name, kind and
    /// <c>partOf</c> from, so a renamed ward advances <c>meta.lastUpdated</c> and an incremental <c>_lastUpdated</c> search
    /// sees its rooms and beds again.
    /// </summary>
    private static DateTimeOffset? Latest(params DateTimeOffset?[] changes) => changes.Max();

    private static R4.Location Place(
        string id, string name, string physicalType, string physicalTypeDisplay, WardType wardType, Guid facilityId, bool isActive,
        DateTimeOffset createdAt, DateTimeOffset? updatedAt)
    {
        var location = new R4.Location
        {
            Id = id,
            Meta = FhirElements.Meta(createdAt, updatedAt),
            Status = isActive ? R4.Location.LocationStatus.Active : R4.Location.LocationStatus.Inactive,
            Name = name,
            Mode = R4.Location.LocationMode.Instance,
            PhysicalType = FhirElements.Concept(FhirSystems.LocationPhysicalType, physicalType, physicalTypeDisplay),
            ManagingOrganization = new R4.ResourceReference($"{FhirResourceTypes.Organization}/{FhirIds.Of(facilityId)}") { Type = FhirResourceTypes.Organization },
        };

        location.Type.Add(ServiceType(wardType));
        return location;
    }

    private static R4.ResourceReference Reference(string id, string display) =>
        new($"{FhirResourceTypes.Location}/{id}") { Type = FhirResourceTypes.Location, Display = display };
}
