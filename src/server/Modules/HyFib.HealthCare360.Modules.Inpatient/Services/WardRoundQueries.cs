using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Rounds;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// Builds the ward round for a doctor or a department within the current facility: the patients still in a bed
/// (admitted or with a discharge in progress), in the order of the bed board, with when they were last seen, the latest
/// note, results not yet reviewed, how worrying the latest vital signs are, and the banner's safety flags. As on the bed
/// board, a restricted (VIP) patient's name, diagnosis and vital-sign details are withheld unless the user may open
/// restricted records; the bed, the UHID and what needs attention stay, and the chart opens with break-the-glass.
/// </summary>
public sealed class WardRoundQueries(HospitalDbContext db, IPermissionService permissions, ICurrentUser currentUser, TimeProvider time)
{
    internal const string RestrictedName = "Restricted patient";

    private const int Limit = 200;
    private const int MaxVitalFlags = 3;

    public async Task<WardRound> BuildAsync(Guid? practitionerId, Guid? departmentId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var today = IndiaClock.LocalDate(now);
        if (practitionerId is null && departmentId is null)
        {
            return new WardRound(today, null, null, []);
        }

        var rows = await Admissions(practitionerId, departmentId).Take(Limit).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return new WardRound(today, practitionerId, departmentId, []);
        }

        var admissionIds = rows.Select(r => r.Admission.Id).ToList();
        var patientIds = rows.Select(r => r.Admission.PatientId).Distinct().ToList();
        var since = rows.Min(r => r.Admission.AdmittedAt);

        var visits = db.Set<ConsultantVisit>().AsNoTracking().Where(v => admissionIds.Contains(v.AdmissionId));
        var notes = db.Set<ClinicalNote>().AsNoTracking().Where(n => patientIds.Contains(n.PatientId) && n.CreatedAt >= since);
        var encounters = db.Set<Encounter>().AsNoTracking().Where(e => patientIds.Contains(e.PatientId) && e.StartedAt >= since);
        if (practitionerId is { } author)
        {
            visits = visits.Where(v => v.PractitionerId == author);
            notes = notes.Where(n => n.AuthorPractitionerId == author);
            encounters = encounters.Where(e => e.PractitionerId == author);
        }

        var lastVisits = await visits.GroupBy(v => v.AdmissionId)
            .Select(g => new { AdmissionId = g.Key, At = g.Max(v => v.VisitedAt) })
            .ToDictionaryAsync(x => x.AdmissionId, x => x.At, ct);
        var lastNotes = await notes.GroupBy(n => n.PatientId)
            .Select(g => new { PatientId = g.Key, At = g.Max(n => n.CreatedAt) })
            .ToDictionaryAsync(x => x.PatientId, x => x.At, ct);
        var lastEncounters = await encounters.GroupBy(e => e.PatientId)
            .Select(g => new { PatientId = g.Key, At = g.Max(e => e.StartedAt) })
            .ToDictionaryAsync(x => x.PatientId, x => x.At, ct);

        // Every result of the stay still waiting for a doctor, whoever ordered it: the consultant answers for them all.
        var results = await db.Set<ResultReview>().AsNoTracking()
            .Where(r => patientIds.Contains(r.PatientId) && r.ReviewedAt == null && r.ReleasedAt >= since)
            .Select(r => new { r.PatientId, r.ReleasedAt, r.HasCriticalFindings })
            .ToListAsync(ct);

        var latestVitals = (await LatestVitals(patientIds, since).ToListAsync(ct)).ToDictionary(v => v.PatientId);

        var canSeeRestricted = currentUser.UserId is { } userId && await permissions.HasPermissionAsync(userId, PatientsPermissions.ReadRestricted, ct);
        var startOfToday = IndiaClock.StartOfDayUtc(today);
        return new WardRound(today, practitionerId, departmentId, rows
            .OrderBy(r => r.WardOrder ?? int.MaxValue)
            .ThenBy(r => r.WardName ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.BedOrder ?? int.MaxValue)
            .ThenBy(r => r.BedLabel ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Admission.AdmittedAt)
            .Select(r =>
            {
                var a = r.Admission;
                DateTimeOffset? lastVisit = lastVisits.TryGetValue(a.Id, out var visited) && visited >= a.AdmittedAt ? visited : null;
                var lastNote = Latest(Since(lastNotes, a.PatientId, a.AdmittedAt), Since(lastEncounters, a.PatientId, a.AdmittedAt));
                var pending = results.Where(x => x.PatientId == a.PatientId && x.ReleasedAt >= a.AdmittedAt).ToList();
                var reading = latestVitals.TryGetValue(a.PatientId, out var v) && v.RecordedAt >= a.AdmittedAt ? v : null;
                var flags = reading is null
                    ? []
                    : VitalSignsAssessor.Assess(reading.ToReading(), r.DateOfBirth, today).OrderByDescending(f => f.Severity).ToList();
                var withheld = r.IsVip && !canSeeRestricted;
                return new WardRoundPatient(
                    a.Id, a.AdmissionNumber, a.PatientId, r.Uhid, withheld ? RestrictedName : r.FullName, r.Gender,
                    r.DateOfBirth is { } dob ? Age.Format(dob, today) : null, a.WardId, r.WardName, r.BedLabel,
                    r.WardType is { } type && Ward.IsCriticalCareType(type), a.Status, a.AdmittedAt,
                    today.DayNumber - IndiaClock.LocalDate(a.AdmittedAt).DayNumber + 1, a.ExpectedDischargeDate, withheld ? null : a.ProvisionalDiagnosis,
                    a.IsMlc, r.IsVip, a.ConsultantPractitionerId, r.ConsultantName, practitionerId is null || a.ConsultantPractitionerId == practitionerId,
                    r.HasAllergies, r.HasCriticalAlerts, lastVisit, lastVisit >= startOfToday, lastNote, pending.Count, pending.Any(x => x.HasCriticalFindings),
                    reading?.RecordedAt, flags.Count == 0 ? null : flags[0].Severity, withheld ? [] : [.. flags.Take(MaxVitalFlags).Select(f => f.Message)]);
            })
            .ToList());
    }

    /// <summary>The patients still in a bed under the doctor (as consultant, or visited during the stay) or in the department.</summary>
    internal IQueryable<RoundRow> Admissions(Guid? practitionerId, Guid? departmentId)
    {
        var admissions = db.Set<Admission>().AsNoTracking()
            .Where(a => a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated);
        admissions = practitionerId is { } doctor
            ? admissions.Where(a => a.ConsultantPractitionerId == doctor || db.Set<ConsultantVisit>().Any(v => v.AdmissionId == a.Id && v.PractitionerId == doctor))
            : admissions.Where(a => a.DepartmentId == departmentId);

        return from a in admissions
               join p in db.Set<Patient>() on a.PatientId equals p.Id
               join w in db.Set<Ward>() on a.WardId equals w.Id into wards
               from w in wards.DefaultIfEmpty()
               join b in db.Set<Bed>() on a.BedId equals b.Id into beds
               from b in beds.DefaultIfEmpty()
               join c in db.Set<Practitioner>() on a.ConsultantPractitionerId equals c.Id into consultants
               from c in consultants.DefaultIfEmpty()
               orderby a.AdmittedAt
               select new RoundRow
               {
                   Admission = a,
                   Uhid = p.Uhid,
                   FullName = p.FullName,
                   IsVip = p.IsVip,
                   Gender = p.Gender,
                   DateOfBirth = p.DateOfBirth,
                   WardName = w != null ? w.Name : null,
                   WardOrder = w != null ? w.DisplayOrder : null,
                   WardType = w != null ? w.Type : null,
                   BedLabel = b != null ? b.Label : null,
                   BedOrder = b != null ? b.DisplayOrder : null,
                   ConsultantName = c != null ? c.DisplayName : null,
                   HasAllergies = db.Set<Allergy>().Any(x => x.PatientId == a.PatientId && x.Status == AllergyStatus.Active),
                   HasCriticalAlerts = db.Set<PatientAlert>().Any(x => x.PatientId == a.PatientId && x.IsActive && x.Severity == AlertSeverity.Critical),
               };
    }

    /// <summary>Each patient's latest vital signs since <paramref name="since"/> (entries marked in error are left out).</summary>
    internal IQueryable<VitalSigns> LatestVitals(IReadOnlyCollection<Guid> patientIds, DateTimeOffset since) =>
        db.Set<VitalSigns>().AsNoTracking()
            .Where(v => patientIds.Contains(v.PatientId) && !v.IsEnteredInError && v.RecordedAt >= since)
            .GroupBy(v => v.PatientId)
            .Select(g => g.OrderByDescending(v => v.RecordedAt).First());

    private static DateTimeOffset? Since(Dictionary<Guid, DateTimeOffset> latest, Guid patientId, DateTimeOffset admittedAt) =>
        latest.TryGetValue(patientId, out var at) && at >= admittedAt ? at : null;

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;
}

internal sealed record RoundRow
{
    public required Admission Admission { get; init; }
    public required string Uhid { get; init; }
    public required string FullName { get; init; }
    public bool IsVip { get; init; }
    public Gender Gender { get; init; }
    public DateOnly? DateOfBirth { get; init; }
    public string? WardName { get; init; }
    public int? WardOrder { get; init; }
    public WardType? WardType { get; init; }
    public string? BedLabel { get; init; }
    public int? BedOrder { get; init; }
    public string? ConsultantName { get; init; }
    public bool HasAllergies { get; init; }
    public bool HasCriticalAlerts { get; init; }
}
