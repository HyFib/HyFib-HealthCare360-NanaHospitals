using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>Facility letterhead printed on prescriptions, discharge summaries and checkup reports (UX-09).</summary>
public sealed record FacilityLetterhead(
    Guid FacilityId,
    string Code,
    string Name,
    string? LegalName,
    string Address,
    string? Phone,
    string? Email,
    string? Website,
    string? RegistrationNumber,
    string? NabhNumber,
    string? HfrId,
    Guid? LogoFileId,
    string? LetterheadNote);

/// <summary>Prescriber block required on Indian prescriptions (name, qualification, council registration number).</summary>
public sealed record PrescriberInfo(
    Guid PractitionerId,
    string DisplayName,
    string? Qualification,
    string? RegistrationCouncil,
    string? RegistrationNumber,
    string? Department,
    string? Specialty,
    Guid? SignatureFileId);

public static class PrintQueries
{
    public static async Task<FacilityLetterhead?> LetterheadAsync(HospitalDbContext db, Guid facilityId, CancellationToken ct)
    {
        var f = await db.Set<Facility>().AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == facilityId, ct);
        return f is null
            ? null
            : new FacilityLetterhead(f.Id, f.Code, f.Name, f.LegalName, f.Address.ToString(), f.Phone, f.Email, f.Website, f.RegistrationNumber,
                f.NabhNumber, f.HfrId, f.LogoFileId, f.LetterheadNote);
    }

    public static async Task<PrescriberInfo?> PrescriberAsync(HospitalDbContext db, Guid practitionerId, CancellationToken ct) =>
        await (
                from p in db.Set<Practitioner>().AsNoTracking()
                join d in db.Set<Department>().IgnoreQueryFilters() on p.DepartmentId equals d.Id into departments
                from d in departments.DefaultIfEmpty()
                join s in db.Set<Specialty>() on p.SpecialtyId equals s.Id into specialties
                from s in specialties.DefaultIfEmpty()
                where p.Id == practitionerId
                select new PrescriberInfo(p.Id, p.DisplayName, p.Qualification, p.RegistrationCouncil, p.RegistrationNumber,
                    d != null ? d.Name : null, s != null ? s.Name : null, p.SignatureFileId))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The prescriber of <paramref name="prescription"/>, with the registration number and council it was signed with
    /// (<see cref="Prescription.PrescriberRegistrationNumber"/>): a reprint, the portal and the QR check show what the
    /// prescription carried, whatever the practitioner record says now. A draft, or a prescription signed before HC360
    /// kept them, shows the record as it is.
    /// </summary>
    public static async Task<PrescriberInfo?> PrescriberAsync(HospitalDbContext db, Prescription prescription, CancellationToken ct)
    {
        var prescriber = await PrescriberAsync(db, prescription.PractitionerId, ct);
        return prescriber is not null && prescription.PrescriberRegistrationNumber is { } number
            ? prescriber with { RegistrationNumber = number, RegistrationCouncil = prescription.PrescriberRegistrationCouncil }
            : prescriber;
    }

    /// <summary>Display names for a set of practitioners (lists and timelines).</summary>
    public static async Task<Dictionary<Guid, string>> PractitionerNamesAsync(HospitalDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (list.Count == 0)
        {
            return [];
        }

        return await db.Set<Practitioner>().AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
    }

    public static async Task<Dictionary<Guid, string>> DepartmentNamesAsync(HospitalDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        if (list.Count == 0)
        {
            return [];
        }

        return await db.Set<Department>().AsNoTracking().IgnoreQueryFilters().Where(d => list.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
    }
}
