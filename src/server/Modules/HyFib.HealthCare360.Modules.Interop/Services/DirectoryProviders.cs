using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
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
