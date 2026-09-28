using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>The patient facts diagnostics needs (reference ranges, labels, reports, notifications).</summary>
internal sealed record PatientSnapshot(
    Guid Id,
    string Uhid,
    string FullName,
    string FirstName,
    string? MiddleName,
    string? LastName,
    Gender Gender,
    DateOnly? DateOfBirth,
    string? Phone,
    string? Email,
    bool IsActive,
    bool IsDeceased,
    Guid? MergedIntoPatientId)
{
    public string? AgeOn(DateOnly onDate) => DateOfBirth is { } dob ? Age.Format(dob, onDate) : null;

    /// <summary>"34Y / M" as printed on labels and report headers.</summary>
    public string AgeSex(DateOnly onDate) => $"{AgeOn(onDate) ?? "-"} / {SexCode}";

    public string SexCode => Gender switch
    {
        Gender.Male => "M",
        Gender.Female => "F",
        Gender.Other => "O",
        _ => "U",
    };
}

internal sealed record PractitionerSnapshot(
    Guid Id, string DisplayName, string? Phone, string? Email, string? Qualification, string? RegistrationNumber, Guid? SignatureFileId);

internal sealed record FacilitySnapshot(
    Guid Id, string Code, string Name, Address Address, string? Phone, string? Email, string? NabhNumber, string? LetterheadNote, Guid? LogoFileId);

/// <summary>A person who signed a report: name plus the practitioner's qualification, registration and signature image.</summary>
internal sealed record Signatory(Guid UserId, string Name, string? Qualification, string? RegistrationNumber, Guid? SignatureFileId);

/// <summary>Read-only look-ups into the Patients module and the platform masters.</summary>
internal sealed class DiagnosticsLookups(HospitalDbContext db)
{
    private readonly Dictionary<(Guid FacilityId, string Code), Guid?> _departments = [];

    public async Task<PatientSnapshot?> PatientAsync(Guid id, CancellationToken ct) =>
        (await PatientsAsync([id], ct)).GetValueOrDefault(id);

    public async Task<IReadOnlyDictionary<Guid, PatientSnapshot>> PatientsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, PatientSnapshot>();
        }

        var list = ids.Distinct().ToList();
        return await db.Set<Patient>().AsNoTracking()
            .Where(p => list.Contains(p.Id))
            .Select(p => new PatientSnapshot(
                p.Id, p.Uhid, p.FullName, p.FirstName, p.MiddleName, p.LastName, p.Gender, p.DateOfBirth, p.Phone, p.Email, p.IsActive,
                p.IsDeceased, p.MergedIntoPatientId))
            .ToDictionaryAsync(p => p.Id, ct);
    }

    /// <summary>Encounter class from the visit type (an admission always makes it in-patient).</summary>
    public async Task<PatientClass> PatientClassAsync(Guid? visitId, Guid? admissionId, CancellationToken ct)
    {
        VisitType? type = null;
        if (visitId is { } id)
        {
            type = await db.Set<Visit>().Where(v => v.Id == id).Select(v => (VisitType?)v.Type).FirstOrDefaultAsync(ct);
        }

        return PatientClasses.From(type, admissionId is not null);
    }

    /// <summary>Department id by code within a facility (LAB, RAD, CDX), cached for the request.</summary>
    public async Task<Guid?> DepartmentIdAsync(Guid facilityId, string code, CancellationToken ct)
    {
        if (_departments.TryGetValue((facilityId, code), out var cached))
        {
            return cached;
        }

        var id = await db.Set<Department>()
            .Where(d => d.FacilityId == facilityId && d.Code == code && d.IsActive)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);
        _departments[(facilityId, code)] = id;
        return id;
    }

    public async Task<PractitionerSnapshot?> PractitionerAsync(Guid? id, CancellationToken ct) =>
        id is not { } practitionerId
            ? null
            : await db.Set<Practitioner>().AsNoTracking()
                .Where(p => p.Id == practitionerId)
                .Select(p => new PractitionerSnapshot(p.Id, p.DisplayName, p.Phone, p.Email, p.Qualification, p.RegistrationNumber, p.SignatureFileId))
                .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, string>> PractitionerNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<Practitioner>().AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
    }

    /// <summary>Staff accounts linked to a practitioner (critical-value alerts go to the ordering doctor's account).</summary>
    public Task<List<Guid>> UserIdsForPractitionerAsync(Guid practitionerId, CancellationToken ct) =>
        db.Set<User>()
            .Where(u => u.PractitionerId == practitionerId && u.IsActive && u.Type == UserType.Staff)
            .Select(u => u.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<User>().AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    /// <summary>
    /// Signature block for the people who verified a report: the user's name, and when the user is a practitioner, the
    /// qualification, council registration number and signature image required on Indian diagnostic reports.
    /// </summary>
    public async Task<IReadOnlyList<Signatory>> SignatoriesAsync(IEnumerable<Guid?> userIds, CancellationToken ct)
    {
        var list = userIds.OfType<Guid>().Distinct().ToList();
        if (list.Count == 0)
        {
            return [];
        }

        return await (
                from u in db.Set<User>().AsNoTracking()
                join p in db.Set<Practitioner>() on u.PractitionerId equals p.Id into practitioners
                from p in practitioners.DefaultIfEmpty()
                where list.Contains(u.Id)
                select new Signatory(
                    u.Id,
                    p != null ? p.DisplayName : u.DisplayName,
                    p != null ? p.Qualification : null,
                    p != null ? p.RegistrationNumber : null,
                    p != null ? p.SignatureFileId : null))
            .ToListAsync(ct);
    }

    public Task<FacilitySnapshot?> FacilityAsync(Guid facilityId, CancellationToken ct) =>
        db.Set<Facility>().AsNoTracking()
            .Where(f => f.Id == facilityId)
            .Select(f => new FacilitySnapshot(f.Id, f.Code, f.Name, f.Address, f.Phone, f.Email, f.NabhNumber, f.LetterheadNote, f.LogoFileId))
            .FirstOrDefaultAsync(ct);
}
