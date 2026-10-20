using System.Security.Cryptography;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using Microsoft.Extensions.Logging;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// Patient reads and searches over the Patients module's master record. The ABHA number is decrypted only to put it in
/// the response (never logged); a value that cannot be decrypted is left out rather than failing the request.
/// </summary>
internal sealed class PatientRecords(
    HospitalDbContext db,
    IFieldProtector protector,
    IPermissionService permissions,
    ICurrentUser currentUser,
    ReferenceDirectoryLoader references,
    ILogger<PatientRecords> logger)
{
    public async Task<R4.Patient?> FindAsync(Guid id, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return patient is null ? null : (await MapAsync([patient], ct))[0];
    }

    /// <summary>
    /// A page of consenting patients matching the criteria. Restricted (VIP) patients are included only for callers with
    /// <c>patients.restricted.read</c>; a break-the-glass grant opens one record by id, not a search.
    /// </summary>
    public async Task<(IReadOnlyList<R4.Patient> Page, int Total)> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var includeRestricted = currentUser.UserId is { } userId && await permissions.HasPermissionAsync(userId, PatientsPermissions.ReadRestricted, ct);
        var query = SearchQuery(request, includeRestricted);
        var total = await query.CountAsync(ct);
        if (request.Count == 0 || total <= request.Offset)
        {
            return ([], total);
        }

        var page = await PatientSearchQuery.Ordered(query).Skip(request.Offset).Take(request.Count).ToListAsync(ct);
        return (await MapAsync(page, ct), total);
    }

    /// <summary>The SQL query of a Patient search (name words match the trigram-indexed full name with ILIKE).</summary>
    internal IQueryable<Patient> SearchQuery(SearchRequest request, bool includeRestricted) =>
        PatientSearchQuery.Apply(
            db.Set<Patient>().AsNoTracking(),
            db.Set<PatientConsent>(),
            PatientCriteria.From(request, protector.BlindIndexes),
            includeRestricted,
            word =>
            {
                var pattern = word.ToLikePattern();
                return p => EF.Functions.ILike(p.FullName, pattern, QueryExtensions.LikeEscape);
            });

    private async Task<List<R4.Patient>> MapAsync(IReadOnlyList<Patient> patients, CancellationToken ct)
    {
        var ids = new ReferenceIds();
        foreach (var patient in patients)
        {
            ids.Facility(patient.RegisteredFacilityId);
        }

        var refs = await references.LoadAsync(ids, ct);
        return [.. patients.Select(p => PatientMapper.Map(ToRow(p), refs))];
    }

    private PatientRow ToRow(Patient p) => new()
    {
        Id = p.Id,
        Uhid = p.Uhid,
        Title = p.Title,
        FirstName = p.FirstName,
        MiddleName = p.MiddleName,
        LastName = p.LastName,
        FullName = p.FullName,
        Gender = p.Gender,
        DateOfBirth = p.DateOfBirth,
        IsDateOfBirthEstimated = p.IsDateOfBirthEstimated,
        MaritalStatus = p.MaritalStatus,
        Phone = p.Phone,
        AlternatePhone = p.AlternatePhone,
        Email = p.Email,
        Address = p.Address,
        GuardianName = p.GuardianName,
        GuardianRelation = p.GuardianRelation,
        EmergencyContactName = p.EmergencyContactName,
        EmergencyContactPhone = p.EmergencyContactPhone,
        EmergencyContactRelation = p.EmergencyContactRelation,
        PreferredLanguage = p.PreferredLanguage,
        AbhaNumber = DecryptAbha(p.AbhaNumberCipher),
        IsDeceased = p.IsDeceased,
        DeceasedAt = p.DeceasedAt,
        IsActive = p.IsActive,
        MergedIntoPatientId = p.MergedIntoPatientId,
        RegisteredFacilityId = p.RegisteredFacilityId,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
    };

    private string? DecryptAbha(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(cipher);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Deliberately no identifier in the message.
            logger.LogWarning("An ABHA number could not be decrypted and was left out of a FHIR Patient resource");
            return null;
        }
    }
}
