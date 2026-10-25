using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The emergency department: each emergency visit opens a case; the triage nurse grades it on the Emergency Severity
/// Index (and the triage vitals go to the patient's chart), the doctor sees the patient and decides where they go, and a
/// medico-legal case is entered in the MLC register and reported to the police. The ER board shows the open cases, most
/// urgent first, with the waits that the department is measured by.
/// </summary>
internal sealed class EmergencyService(
    HospitalDbContext db,
    INumberSeries numbers,
    PatientAccessGuard guard,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string ChangedEvent = "er.changed";
    private const string Restricted = "Restricted";

    public Task<EmergencyCase?> FindAsync(Guid id, CancellationToken ct) => db.Set<EmergencyCase>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<ErBoard> BoardAsync(CancellationToken ct)
    {
        var cases = await db.Set<EmergencyCase>().AsNoTracking().Where(c => c.Disposition == null).ToListAsync(ct);
        var caseIds = cases.Select(c => c.Id).ToList();
        var complaints = (await db.Set<EsiTriage>().AsNoTracking().Where(t => caseIds.Contains(t.CaseId))
                .Select(t => new { t.CaseId, t.AssessedAt, t.ChiefComplaint }).ToListAsync(ct))
            .GroupBy(t => t.CaseId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.AssessedAt).First().ChiefComplaint);
        var visitIds = cases.Select(c => c.VisitId).ToList();
        var visits = await db.Set<Visit>().AsNoTracking().Where(v => visitIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, v => new { v.ChiefComplaint, v.IsMlc, v.MlcNumber }, ct);
        var mlcs = await db.Set<MlcRecord>().AsNoTracking().Where(m => visitIds.Contains(m.VisitId)).ToDictionaryAsync(m => m.VisitId, ct);
        var people = await PeopleAsync(cases.Select(c => c.PatientId), ct);
        var now = time.GetUtcNow();
        var rows = cases
            .Select(c =>
            {
                var person = people[c.PatientId];
                var visit = visits.GetValueOrDefault(c.VisitId);
                var mlc = mlcs.GetValueOrDefault(c.VisitId);
                return new ErBoardRow(
                    c.Id, c.VisitId, c.PatientId, person.Name, person.Uhid, person.Gender, person.AgeYears, c.ArrivedAt, c.ArrivalMode, c.Level,
                    complaints.GetValueOrDefault(c.Id) ?? visit?.ChiefComplaint, Minutes(c.ArrivedAt, now), c.TriagedAt is { } t ? Minutes(c.ArrivedAt, t) : null,
                    c.DoctorSeenAt is { } d ? Minutes(c.ArrivedAt, d) : null, c.DoctorName, visit?.IsMlc ?? false, mlc?.Number ?? visit?.MlcNumber,
                    mlc?.IntimationOverdue(now) ?? false, c.Version);
            })
            .OrderBy(r => r.Level ?? 0)
            .ThenBy(r => r.ArrivedAt)
            .ToList();
        return new ErBoard(rows, rows.Count(r => r.Level is null), [.. Enumerable.Range(1, 5).Select(l => rows.Count(r => r.Level == l))]);
    }

    public async Task<EmergencyCaseDetail> DetailAsync(EmergencyCase c, CancellationToken ct)
    {
        var person = (await PeopleAsync([c.PatientId], ct))[c.PatientId];
        var triages = await db.Set<EsiTriage>().AsNoTracking().Where(t => t.CaseId == c.Id).OrderByDescending(t => t.AssessedAt).ToListAsync(ct);
        var mlc = await db.Set<MlcRecord>().AsNoTracking().FirstOrDefaultAsync(m => m.VisitId == c.VisitId, ct);
        return new EmergencyCaseDetail(
            c.Id, c.VisitId, c.PatientId, person.Name, person.Uhid, person.Gender, person.AgeYears, c.ArrivedAt, c.ArrivalMode, c.Level, c.TriagedAt,
            c.DoctorSeenAt, c.DoctorName, c.Disposition, c.DisposedAt, c.DisposedByName, c.DispositionNotes, c.ReferredTo,
            triages.Select(Dto).ToList(), mlc is null ? null : Dto(mlc), c.Version);
    }

    public async Task<TriagePreview> PreviewAsync(EmergencyCase c, TriagePreviewRequest r, CancellationToken ct)
    {
        var assessment = EmergencySeverityIndex.Assess(
            new EsiAnswers(r.NeedsLifeSavingIntervention, r.HighRiskSituation, r.ConfusedLethargicOrDisoriented, r.SevereDistress, r.ExpectedResources),
            new TriageVitals(r.Pulse, r.RespiratoryRate, r.SpO2, r.SystolicBp, r.DiastolicBp, r.TemperatureCelsius, r.Gcs, r.PainScore),
            await AgeInDaysAsync(c.PatientId, ct));
        return new TriagePreview(assessment.Level, assessment.Reasons, assessment.DangerZoneVitals);
    }

    public async Task<Result<EmergencyCaseDetail>> TriageAsync(Guid id, TriageRequest request, CancellationToken ct)
    {
        var c = await db.Set<EmergencyCase>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("Emergency case", id);
        }

        db.SetExpectedVersion(c, request.Version);
        var now = time.GetUtcNow();
        var assessed = EsiTriage.Assess(
            c.Id, request.ChiefComplaint, Answers(request), Vitals(request), await AgeInDaysAsync(c.PatientId, ct), request.Level, request.OverrideReason,
            Me, now);
        if (assessed.IsFailure)
        {
            return assessed.Error!;
        }

        var triage = assessed.Value;
        var triaged = c.Triage(triage, request.ArrivalMode);
        if (triaged.IsFailure)
        {
            return triaged.Error!;
        }

        // The triage vitals are the patient's first vitals in the ER: they go to the chart once, not typed again.
        if (request is { Pulse: not null } or { RespiratoryRate: not null } or { SpO2: not null } or { SystolicBp: not null }
            or { TemperatureCelsius: not null } or { Gcs: not null } or { PainScore: not null })
        {
            var vitals = new VitalSigns
            {
                PatientId = c.PatientId,
                FacilityId = c.FacilityId,
                RecordedAt = now,
                RecordedByUserId = currentUser.UserId,
                Pulse = request.Pulse,
                RespiratoryRate = request.RespiratoryRate,
                SpO2 = request.SpO2,
                SystolicBp = request.SystolicBp,
                DiastolicBp = request.DiastolicBp,
                Temperature = request.TemperatureCelsius,
                TemperatureUnit = TemperatureUnit.Celsius,
                Gcs = request.Gcs,
                PainScore = request.PainScore,
                Notes = $"ER triage, ESI level {triage.Level}",
            };
            db.Add(vitals);
            triage.VitalSignsId = vitals.Id;
        }

        db.Add(triage);
        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == c.VisitId, ct);
        if (visit is not null && string.IsNullOrWhiteSpace(visit.ChiefComplaint))
        {
            visit.ChiefComplaint = triage.ChiefComplaint;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(c, ct);
        return await DetailAsync(c, ct);
    }

    public async Task<Result<EmergencyCaseDetail>> SeenAsync(Guid id, uint version, CancellationToken ct)
    {
        var c = await db.Set<EmergencyCase>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("Emergency case", id);
        }

        db.SetExpectedVersion(c, version);
        var now = time.GetUtcNow();
        var seen = c.SeenBy(Me, now);
        if (seen.IsFailure)
        {
            return seen.Error!;
        }

        (await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == c.VisitId, ct))?.Start();
        await db.SaveChangesAsync(ct);
        await PublishAsync(c, ct);
        return await DetailAsync(c, ct);
    }

    /// <summary>
    /// Where the patient goes. The emergency visit closes with it: an admitted patient's stay goes on as the admission's
    /// own visit, which the admission desk opens from the bed.
    /// </summary>
    public async Task<Result<EmergencyCaseDetail>> DisposeAsync(Guid id, DispositionRequest request, CancellationToken ct)
    {
        var c = await db.Set<EmergencyCase>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("Emergency case", id);
        }

        db.SetExpectedVersion(c, request.Version);
        var now = time.GetUtcNow();
        var disposed = c.Dispose(request.Disposition, request.Notes, request.ReferredTo, Me, now);
        if (disposed.IsFailure)
        {
            return disposed.Error!;
        }

        if (await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == c.VisitId, ct) is { } visit)
        {
            var completed = visit.Complete(now);
            if (completed.IsFailure)
            {
                return completed.Error!;
            }
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(c, ct);
        return await DetailAsync(c, ct);
    }

    /// <summary>Opens the case's MLC entry (numbered in the register, and marked on the visit) or corrects it before the police are told.</summary>
    public async Task<Result<EmergencyCaseDetail>> SaveMlcAsync(Guid id, SaveMlcRequest request, CancellationToken ct)
    {
        var c = await db.Set<EmergencyCase>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("Emergency case", id);
        }

        var now = time.GetUtcNow();
        var mlc = await db.Set<MlcRecord>().FirstOrDefaultAsync(m => m.VisitId == c.VisitId, ct);
        if (mlc is null)
        {
            if (request.Version is not null)
            {
                return Error.NotFound("MLC entry", id);
            }

            mlc = new MlcRecord
            {
                FacilityId = c.FacilityId,
                PatientId = c.PatientId,
                VisitId = c.VisitId,
                Number = await numbers.NextAsync(ClinicalNumberSeries.MlcRegister, c.FacilityId, ct),
                IdentifiedAt = now,
            };
            db.Add(mlc);
        }
        else
        {
            if (request.Version is not { } version)
            {
                return Error.Conflict("mlc.exists", "This case is already in the MLC register; correct its entry instead.");
            }

            db.SetExpectedVersion(mlc, version);
        }

        var described = mlc.Describe(
            request.BroughtBy, request.BroughtByName, request.Cause, request.IncidentAt, request.IncidentPlace, request.Injuries, request.ConsciousOnArrival,
            request.SmellOfAlcohol, request.PoliceStation, now);
        if (described.IsFailure)
        {
            return described.Error!;
        }

        mlc.DoctorName = Me;
        mlc.DoctorRegistrationNumber = string.IsNullOrWhiteSpace(request.DoctorRegistrationNumber) ? null : request.DoctorRegistrationNumber.Trim();
        if (await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == c.VisitId, ct) is { } visit)
        {
            visit.IsMlc = true;
            visit.MlcNumber = mlc.Number;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(c, ct);
        return await DetailAsync(c, ct);
    }

    public async Task<Result<EmergencyCaseDetail>> IntimateAsync(Guid id, MlcIntimationRequest request, CancellationToken ct)
    {
        var c = await db.Set<EmergencyCase>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("Emergency case", id);
        }

        var mlc = await db.Set<MlcRecord>().FirstOrDefaultAsync(m => m.VisitId == c.VisitId, ct);
        if (mlc is null)
        {
            return Error.Rule("mlc.missing", "Enter the case in the MLC register first.");
        }

        db.SetExpectedVersion(mlc, request.Version);
        var intimated = mlc.Intimate(request.IntimatedAt, request.Mode, request.ReceivedBy, request.PoliceEntryNumber, time.GetUtcNow());
        if (intimated.IsFailure)
        {
            return intimated.Error!;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(c, ct);
        return await DetailAsync(c, ct);
    }

    /// <summary>The MLC register: every entry identified from <paramref name="from"/> to <paramref name="to"/> (hospital days).</summary>
    public async Task<IReadOnlyList<MlcRegisterRow>> RegisterAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var start = StartOfDay(from);
        var end = StartOfDay(to.AddDays(1));
        var entries = await db.Set<MlcRecord>().AsNoTracking().Where(m => m.IdentifiedAt >= start && m.IdentifiedAt < end)
            .OrderBy(m => m.IdentifiedAt).ToListAsync(ct);
        var visitIds = entries.Select(e => e.VisitId).ToList();
        var cases = await db.Set<EmergencyCase>().AsNoTracking().Where(c => visitIds.Contains(c.VisitId)).ToDictionaryAsync(c => c.VisitId, c => c.Id, ct);
        var people = await PeopleAsync(entries.Select(e => e.PatientId), ct);
        var now = time.GetUtcNow();
        return entries.Select(m => new MlcRegisterRow(
            m.Id, cases.GetValueOrDefault(m.VisitId), m.Number, m.IdentifiedAt, people[m.PatientId].Name, people[m.PatientId].Uhid, m.Cause, m.PoliceStation,
            m.IntimatedAt, m.ReceivedBy, m.IntimationOverdue(now), m.DoctorName)).ToList();
    }

    public async Task<(MlcRecord Entry, Patient Patient)?> MlcAsync(EmergencyCase c, CancellationToken ct)
    {
        var mlc = await db.Set<MlcRecord>().AsNoTracking().FirstOrDefaultAsync(m => m.VisitId == c.VisitId, ct);
        return mlc is null ? null : (mlc, await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == c.PatientId, ct));
    }

    private string Me => currentUser.DisplayName ?? currentUser.UserName ?? "Staff";

    private Task PublishAsync(EmergencyCase c, CancellationToken ct) => realtime.ToFacilityAsync(c.FacilityId, ChangedEvent, new { CaseId = c.Id }, ct);

    private sealed record Person(string Name, string Uhid, string? Gender, int? AgeYears);

    /// <summary>The patients' names; a restricted (VIP) patient the user may not open shows initials (R-3).</summary>
    private async Task<Dictionary<Guid, Person>> PeopleAsync(IEnumerable<Guid> patientIds, CancellationToken ct)
    {
        var ids = patientIds.Distinct().ToList();
        var patients = await db.Set<Patient>().AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var vip = patients.Values.Where(p => p.IsVip).Select(p => p.Id).ToList();
        var openable = vip.Count == 0 ? new HashSet<Guid>() : (await guard.OpenableAsync(vip, ct)).ToHashSet();
        var today = time.Today();
        return ids.ToDictionary(id => id, id =>
        {
            var p = patients.GetValueOrDefault(id);
            if (p is null)
            {
                return new Person(string.Empty, string.Empty, null, null);
            }

            var hidden = p.IsVip && !openable.Contains(p.Id);
            int? age = p.DateOfBirth is { } dob ? today.Year - dob.Year - (today < dob.AddYears(today.Year - dob.Year) ? 1 : 0) : null;
            return new Person(hidden ? PatientNames.Restricted(p.FullName) : p.FullName, hidden ? Restricted : p.Uhid, p.Gender.ToString(), age);
        });
    }

    private async Task<int?> AgeInDaysAsync(Guid patientId, CancellationToken ct)
    {
        var dateOfBirth = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => p.DateOfBirth).FirstOrDefaultAsync(ct);
        return dateOfBirth is { } dob ? time.Today().DayNumber - dob.DayNumber : null;
    }

    /// <summary>The instant an Indian calendar day begins.</summary>
    private static DateTimeOffset StartOfDay(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(5.5));

    private static int Minutes(DateTimeOffset from, DateTimeOffset to) => (int)Math.Max(0, (to - from).TotalMinutes);

    private static EsiAnswers Answers(TriageRequest r) =>
        new(r.NeedsLifeSavingIntervention, r.HighRiskSituation, r.ConfusedLethargicOrDisoriented, r.SevereDistress, r.ExpectedResources);

    private static TriageVitals Vitals(TriageRequest r) =>
        new(r.Pulse, r.RespiratoryRate, r.SpO2, r.SystolicBp, r.DiastolicBp, r.TemperatureCelsius, r.Gcs, r.PainScore);

    private static EsiTriageDto Dto(EsiTriage t) => new(
        t.Id, t.AssessedAt, t.AssessedByName, t.ChiefComplaint, t.ComputedLevel, t.Level, t.Reasons.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        t.OverrideReason, t.Pulse, t.RespiratoryRate, t.SpO2, t.SystolicBp, t.DiastolicBp, t.TemperatureCelsius, t.Gcs, t.PainScore);

    private MlcDto Dto(MlcRecord m) => new(
        m.Id, m.Number, m.IdentifiedAt, m.BroughtBy, m.BroughtByName, m.Cause, m.IncidentAt, m.IncidentPlace, m.Injuries, m.ConsciousOnArrival,
        m.SmellOfAlcohol, m.DoctorName, m.DoctorRegistrationNumber, m.PoliceStation, m.IntimatedAt, m.IntimationMode, m.ReceivedBy, m.PoliceEntryNumber,
        m.IntimationOverdue(time.GetUtcNow()), m.Version);
}

/// <summary>An emergency visit opens its case in the ER, in the visit's own transaction, so the board never misses an arrival.</summary>
internal sealed class EmergencyCaseOpener(HospitalDbContext db, TimeProvider time) : IDomainEventHandler<VisitCreated>
{
    public async Task HandleAsync(VisitCreated created, CancellationToken cancellationToken)
    {
        if (created.Type != VisitType.Emergency || await db.Set<EmergencyCase>().AnyAsync(c => c.VisitId == created.VisitId, cancellationToken))
        {
            return;
        }

        var visit = await db.Set<Visit>().FindAsync([created.VisitId], cancellationToken);
        db.Add(new EmergencyCase
        {
            FacilityId = created.FacilityId,
            PatientId = created.PatientId,
            VisitId = created.VisitId,
            ArrivedAt = visit?.VisitedAt ?? time.GetUtcNow(),
            ArrivalMode = visit?.ReferralSource == ReferralSource.Doctor || visit?.ReferralSource == ReferralSource.Hospital ? ArrivalMode.ReferredIn : ArrivalMode.WalkIn,
        });
    }
}
