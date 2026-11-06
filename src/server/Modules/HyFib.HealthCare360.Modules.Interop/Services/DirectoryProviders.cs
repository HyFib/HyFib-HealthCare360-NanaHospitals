using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>A directory resource read by id: the resource, or one deleted (still referred to by records), or none.</summary>
internal sealed record DirectoryLookup(R4.Resource? Resource, bool IsDeleted)
{
    public static readonly DirectoryLookup Missing = new(null, false);

    public static readonly DirectoryLookup Deleted = new(null, true);
}

/// <summary>
/// Loads one type of the hospital's directory (<see cref="FhirResourceTypes.Directory"/>). Nothing in it is a patient's,
/// so no consent is asked for it: it says who and where the people and places in the records are, as the hospital's
/// ABDM documents already do.
/// </summary>
internal interface IFhirDirectoryProvider
{
    string ResourceType { get; }

    /// <summary>The resource with this id; one deleted is Gone, as the records that name it still refer to it.</summary>
    Task<DirectoryLookup> FindAsync(string id, CancellationToken ct);

    /// <summary>Every resource of this type a search could match, deleted ones never; the search is run on them in memory.</summary>
    Task<IReadOnlyList<R4.Resource>> ListAsync(CancellationToken ct);
}

/// <summary>
/// The hospital's practitioners, as its ABDM documents carry them (<see cref="AbdmParticipants.Practitioner"/>): name,
/// gender, qualification, HPR ID, council registration and staff code, and whether they are in use. Never a phone
/// number, e-mail, photograph or signature: who a practitioner is, not how to reach them.
/// </summary>
internal sealed class PractitionerDirectory(HospitalDbContext db, ReferenceDirectoryLoader loader) : IFhirDirectoryProvider
{
    public string ResourceType => FhirResourceTypes.Practitioner;

    public async Task<DirectoryLookup> FindAsync(string id, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var key) || (await loader.PractitionersAsync([key], ct)).SingleOrDefault() is not { } row)
        {
            return DirectoryLookup.Missing;
        }

        return row.IsDeleted ? DirectoryLookup.Deleted : new DirectoryLookup(Map(row), false);
    }

    public async Task<IReadOnlyList<R4.Resource>> ListAsync(CancellationToken ct)
    {
        var ids = await db.Set<Practitioner>().AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        return [.. (await loader.PractitionersAsync(ids, ct)).Where(row => !row.IsDeleted).Select(Map)];
    }

    internal static R4.Practitioner Map(PractitionerRow row)
    {
        var practitioner = AbdmParticipants.Practitioner(row);
        practitioner.Active = row.IsActive;
        return practitioner;
    }
}

/// <summary>
/// The hospital's facilities, as its ABDM documents carry them (<see cref="AbdmParticipants.Organization"/>): name, HFR
/// id, facility code, the facility's own phone, e-mail and address, and whether it is in use.
/// </summary>
internal sealed class OrganizationDirectory(HospitalDbContext db, ReferenceDirectoryLoader loader) : IFhirDirectoryProvider
{
    public string ResourceType => FhirResourceTypes.Organization;

    public async Task<DirectoryLookup> FindAsync(string id, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var key) || (await loader.FacilitiesAsync([key], ct)).SingleOrDefault() is not { } row)
        {
            return DirectoryLookup.Missing;
        }

        return row.IsDeleted ? DirectoryLookup.Deleted : new DirectoryLookup(Map(row), false);
    }

    public async Task<IReadOnlyList<R4.Resource>> ListAsync(CancellationToken ct)
    {
        var ids = await db.Set<Facility>().AsNoTracking().Select(f => f.Id).ToListAsync(ct);
        return [.. (await loader.FacilitiesAsync(ids, ct)).Where(row => !row.IsDeleted).Select(Map)];
    }

    internal static R4.Organization Map(FacilityRow row)
    {
        var organization = AbdmParticipants.Organization(row);
        organization.Active = row.IsActive;
        return organization;
    }
}

/// <summary>
/// The places of care (<see cref="LocationMapper"/>): wards, the rooms in them and their beds, read by their
/// <c>ward-</c>, <c>room-</c> and <c>bed-</c> ids. They keep the facility filter, as the stays that name them do: a reader
/// sees its own facility's places, as it sees its own facility's stays. A deleted place (only ever recorded by mistake:
/// the app takes places out of use, it does not delete them) is Gone by id and in no search.
/// </summary>
internal sealed class LocationDirectory(HospitalDbContext db) : IFhirDirectoryProvider
{
    public string ResourceType => FhirResourceTypes.Location;

    public async Task<DirectoryLookup> FindAsync(string id, CancellationToken ct)
    {
        if (FhirIds.TryParse(id, FhirIds.Ward, out var wardId))
        {
            return await Wards(withDeleted: true).FirstOrDefaultAsync(w => w.Id == wardId, ct) is { } ward
                ? Found(ward.IsDeleted, () => LocationMapper.Ward(ward))
                : DirectoryLookup.Missing;
        }

        if (FhirIds.TryParse(id, FhirIds.Room, out var roomId))
        {
            if (await Rooms(withDeleted: true).FirstOrDefaultAsync(r => r.Id == roomId, ct) is not { } room)
            {
                return DirectoryLookup.Missing;
            }

            return await Wards(withDeleted: true).FirstOrDefaultAsync(w => w.Id == room.WardId, ct) is { } ward
                ? Found(room.IsDeleted, () => LocationMapper.Room(room, ward))
                : DirectoryLookup.Missing;
        }

        if (FhirIds.TryParse(id, FhirIds.Bed, out var bedId))
        {
            if (await Beds(withDeleted: true).FirstOrDefaultAsync(b => b.Id == bedId, ct) is not { } bed)
            {
                return DirectoryLookup.Missing;
            }

            var ward = await Wards(withDeleted: true).FirstOrDefaultAsync(w => w.Id == bed.WardId, ct);
            var room = bed.RoomId is { } inRoom ? await Rooms(withDeleted: true).FirstOrDefaultAsync(r => r.Id == inRoom, ct) : null;
            return ward is null ? DirectoryLookup.Missing : Found(bed.IsDeleted, () => LocationMapper.Bed(bed, ward, room));
        }

        return DirectoryLookup.Missing;
    }

    public async Task<IReadOnlyList<R4.Resource>> ListAsync(CancellationToken ct)
    {
        // Every ward the places refer to (deleted ones too, for a live room or bed under one), but only live places listed.
        var wards = (await Wards(withDeleted: true).ToListAsync(ct)).ToDictionary(w => w.Id);
        var rooms = (await Rooms(withDeleted: true).ToListAsync(ct)).ToDictionary(r => r.Id);
        var beds = await Beds(withDeleted: false).ToListAsync(ct);

        return
        [
            .. wards.Values.Where(w => !w.IsDeleted).Select(w => (R4.Resource)LocationMapper.Ward(w)),
            .. rooms.Values.Where(r => !r.IsDeleted && wards.ContainsKey(r.WardId)).Select(r => LocationMapper.Room(r, wards[r.WardId])),
            .. beds.Where(b => wards.ContainsKey(b.WardId))
                .Select(b => LocationMapper.Bed(b, wards[b.WardId], b.RoomId is { } room && rooms.TryGetValue(room, out var r) ? r : null)),
        ];
    }

    private static DirectoryLookup Found(bool deleted, Func<R4.Resource> map) => deleted ? DirectoryLookup.Deleted : new DirectoryLookup(map(), false);

    private IQueryable<T> Places<T>(bool withDeleted)
        where T : class =>
        withDeleted ? db.Set<T>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking() : db.Set<T>().AsNoTracking();

    private IQueryable<WardRow> Wards(bool withDeleted) =>
        Places<Ward>(withDeleted).Select(w => new WardRow
        {
            Id = w.Id,
            FacilityId = w.FacilityId,
            Code = w.Code,
            Name = w.Name,
            Type = w.Type,
            IsActive = w.IsActive,
            IsDeleted = w.IsDeleted,
            CreatedAt = w.CreatedAt,
            UpdatedAt = w.UpdatedAt,
        });

    private IQueryable<RoomRow> Rooms(bool withDeleted) =>
        Places<Room>(withDeleted).Select(r => new RoomRow
        {
            Id = r.Id,
            FacilityId = r.FacilityId,
            WardId = r.WardId,
            Number = r.Number,
            IsActive = r.IsActive,
            IsDeleted = r.IsDeleted,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        });

    private IQueryable<BedRow> Beds(bool withDeleted) =>
        Places<Bed>(withDeleted).Select(b => new BedRow
        {
            Id = b.Id,
            FacilityId = b.FacilityId,
            WardId = b.WardId,
            RoomId = b.RoomId,
            Label = b.Label,
            IsActive = b.IsActive,
            IsDeleted = b.IsDeleted,
            CreatedAt = b.CreatedAt,
            UpdatedAt = b.UpdatedAt,
        });
}
