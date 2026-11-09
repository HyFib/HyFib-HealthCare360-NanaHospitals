using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Realtime;
using Npgsql;

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
    AgainstAdviceDeclarations againstAdvice,
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
        var bayIds = cases.Where(c => c.BayId != null).Select(c => c.BayId!.Value).ToList();
        var bays = await db.Set<ErBay>().AsNoTracking().Where(b => bayIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var now = time.GetUtcNow();
        var rows = cases
            .Select(c =>
            {
                var person = people[c.PatientId];
                var visit = visits.GetValueOrDefault(c.VisitId);
                var mlc = mlcs.GetValueOrDefault(c.VisitId);
                return new ErBoardRow(
                    c.Id, c.VisitId, c.PatientId, person.Name, person.Uhid, person.Gender?.ToString(), person.AgeYears, c.ArrivedAt, c.ArrivalMode, c.Level,
                    complaints.GetValueOrDefault(c.Id) ?? visit?.ChiefComplaint, Minutes(c.ArrivedAt, now), c.TriagedAt is { } t ? Minutes(c.ArrivedAt, t) : null,
                    c.DoctorSeenAt is { } d ? Minutes(c.ArrivedAt, d) : null, c.DoctorName, visit?.IsMlc ?? false, mlc?.Number ?? visit?.MlcNumber,
                    mlc?.IntimationOverdue(now) ?? false, c.Version, c.BayId, c.BayId is { } bay ? bays.GetValueOrDefault(bay) : null);
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
        var bayCode = c.BayId is { } bayId ? await db.Set<ErBay>().AsNoTracking().Where(b => b.Id == bayId).Select(b => b.Code).FirstOrDefaultAsync(ct) : null;
        var wound = mlc is null ? null : await db.Set<WoundCertificate>().AsNoTracking().FirstOrDefaultAsync(w => w.MlcRecordId == mlc.Id, ct);
        return new EmergencyCaseDetail(
            c.Id, c.VisitId, c.PatientId, person.Name, person.Uhid, person.Gender?.ToString(), person.AgeYears, c.ArrivedAt, c.ArrivalMode, c.Level, c.TriagedAt,
            c.DoctorSeenAt, c.DoctorName, c.Disposition, c.DisposedAt, c.DisposedByName, c.DispositionNotes, c.ReferredTo,
            triages.Select(Dto).ToList(), mlc is null ? null : Dto(mlc, person.Gender), c.Version,
            c.LamaSignedBy is { } signedBy
                ? new AgainstAdviceDto(signedBy, c.LamaSignatoryName, c.LamaRelationship, c.LamaDoctorName ?? string.Empty, c.LamaWitnessName ?? string.Empty, c.LamaSignedFormFileId)
                : null,
            c.BayId, bayCode, c.BayAssignedAt, wound is null ? null : Dto(wound));
    }

    /// <summary>The department's bays in board order, with who is in each now.</summary>
    public async Task<ErBays> BaysAsync(CancellationToken ct)
    {
        var bays = await db.Set<ErBay>().AsNoTracking().OrderBy(b => b.DisplayOrder).ThenBy(b => b.Code).ToListAsync(ct);
        var bayIds = bays.Select(b => b.Id).ToList();
        var occupants = await db.Set<EmergencyCase>().AsNoTracking().Where(c => c.Disposition == null && c.BayId != null && bayIds.Contains(c.BayId.Value))
            .ToDictionaryAsync(c => c.BayId!.Value, ct);
        var people = await PeopleAsync(occupants.Values.Select(c => c.PatientId), ct);
        var rows = bays.Select(b =>
        {
            var c = occupants.GetValueOrDefault(b.Id);
            var person = c is null ? null : people[c.PatientId];
            return new ErBayDto(b.Id, b.Code, b.Area, b.DisplayOrder, b.IsActive, c?.Id, person?.Name, person?.Uhid, c?.Level, c?.BayAssignedAt, b.Version);
        }).ToList();
        return new ErBays(rows, rows.Count(r => r.IsActive && r.CaseId is null), rows.Count(r => r.CaseId is not null));
    }

    /// <summary>Adds a bay to the department (<paramref name="id"/> null) or changes one.</summary>
    public async Task<Result<ErBayDto>> SaveBayAsync(Guid? id, SaveErBayRequest request, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        ErBay? bay;
        if (id is null)
        {
            if (currentUser.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.");
            }

            bay = new ErBay { FacilityId = facilityId };
            db.Add(bay);
        }
        else
        {
            bay = await db.Set<ErBay>().FirstOrDefaultAsync(b => b.Id == id, ct);
            if (bay is null)
            {
                return Error.NotFound("ER bay", id.Value);
            }

            if (request.Version is not { } version)
            {
                return Error.Field("version", "Send the version of the bay you edited.");
            }

            db.SetExpectedVersion(bay, version);
        }

        if (await db.Set<ErBay>().AnyAsync(b => b.Code == code && b.Id != bay.Id, ct))
        {
            return Error.Conflict("er.bay_code_taken", $"There is already a bay {code}.");
        }

        (bay.Code, bay.Area, bay.DisplayOrder, bay.IsActive) = (code, request.Area, request.DisplayOrder, request.IsActive);
        await db.SaveChangesAsync(ct);
        await realtime.ToFacilityAsync(bay.FacilityId, ChangedEvent, new { BayId = bay.Id }, ct);
        return new ErBayDto(bay.Id, bay.Code, bay.Area, bay.DisplayOrder, bay.IsActive, null, null, null, null, null, bay.Version);
    }

    /// <summary>
    /// Puts the patient in a bay, or moves them from theirs to another: a bay holds one patient, so a taken bay is refused
    /// (409 <c>er.bay_taken</c>, naming the case in it), as is one out of use.
    /// </summary>
    public async Task<Result<EmergencyCaseDetail>> AssignBayAsync(Guid id, AssignBayRequest request, CancellationToken ct)
    {
        var c = await db.Set<EmergencyCase>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("Emergency case", id);
        }

        db.SetExpectedVersion(c, request.Version);
        var bay = await db.Set<ErBay>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BayId, ct);
        if (bay is null)
        {
            return Error.Field("bayId", "Choose a bay of this emergency department.");
        }

        if (await db.Set<EmergencyCase>().AsNoTracking().Where(o => o.BayId == bay.Id && o.Id != c.Id && o.Disposition == null)
                .Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct) is { } taken)
        {
            return BayTaken(bay.Code, taken);
        }

        var put = c.PutInBay(bay, time.GetUtcNow());
        if (put.IsFailure)
        {
            return put.Error!;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another nurse put a patient in the bay a moment ago.
            return BayTaken(bay.Code, null);
        }

        await PublishAsync(c, ct);
        return await DetailAsync(c, ct);
    }

    /// <summary>Frees the patient's bay while they stay in the department (gone for a scan, moved to a chair).</summary>
    public async Task<Result<EmergencyCaseDetail>> ReleaseBayAsync(Guid id, uint version, CancellationToken ct)
    {
        var c = await db.Set<EmergencyCase>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("Emergency case", id);
        }

        db.SetExpectedVersion(c, version);
        var released = c.ReleaseBay();
        if (released.IsFailure)
        {
            return released.Error!;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(c, ct);
        return await DetailAsync(c, ct);
    }

    /// <summary>
    /// Writes the case's wound certificate while it is a draft: the MLC entry must exist, as the certificate is its
    /// examination. Signed, it is fixed (422 <c>wound_certificate.signed</c>).
    /// </summary>
    public async Task<Result<EmergencyCaseDetail>> SaveWoundCertificateAsync(Guid id, SaveWoundCertificateRequest request, CancellationToken ct)
    {
        var c = await db.Set<EmergencyCase>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("Emergency case", id);
        }

        var mlc = await db.Set<MlcRecord>().AsNoTracking().FirstOrDefaultAsync(m => m.VisitId == c.VisitId, ct);
        if (mlc is null)
        {
            return Error.Rule("mlc.missing", "Enter the case in the MLC register first.");
        }

        var certificate = await db.Set<WoundCertificate>().FirstOrDefaultAsync(w => w.MlcRecordId == mlc.Id, ct);
        if (certificate is null)
        {
            if (request.Version is not null)
            {
                return Error.NotFound("Wound certificate", id);
            }

            certificate = new WoundCertificate { FacilityId = mlc.FacilityId, PatientId = mlc.PatientId, VisitId = mlc.VisitId, MlcRecordId = mlc.Id };
            db.Add(certificate);
        }
        else
        {
            if (request.Version is not { } version)
            {
                return Error.Conflict("wound_certificate.exists", "This case already has a wound certificate; correct it instead.");
            }

            db.SetExpectedVersion(certificate, version);
        }

        var written = certificate.Write(request.ExaminedAt, request.IdentificationMarks, request.History, request.Injuries, request.Remarks, time.GetUtcNow());
        if (written.IsFailure)
        {
            return written.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DetailAsync(c, ct);
    }

    /// <summary>
    /// The examining doctor signs the wound certificate from their own sign-in: an account linked to a doctor in the
    /// practitioner master, whose name and registration are printed on it (403 <c>wound_certificate.doctor_only</c>).
    /// </summary>
    public async Task<Result<EmergencyCaseDetail>> SignWoundCertificateAsync(Guid id, uint version, CancellationToken ct)
    {
        var c = await db.Set<EmergencyCase>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound("Emergency case", id);
        }

        var certificate = await (from w in db.Set<WoundCertificate>()
                                 join m in db.Set<MlcRecord>() on w.MlcRecordId equals m.Id
                                 where m.VisitId == c.VisitId
                                 select w).FirstOrDefaultAsync(ct);
        if (certificate is null)
        {
            return Error.Rule("wound_certificate.missing", "Write the wound certificate first.");
        }

        var doctor = currentUser.PractitionerId is { } practitionerId
            ? await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == practitionerId && p.IsActive && Doctors.Contains(p.Kind))
                .Select(p => new { p.Id, p.DisplayName, p.RegistrationCouncil, p.RegistrationNumber }).FirstOrDefaultAsync(ct)
            : null;
        if (doctor is null || currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("wound_certificate.doctor_only",
                "The examining doctor signs the wound certificate from their own sign-in: your account is not linked to a doctor in the practitioner list.");
        }

        db.SetExpectedVersion(certificate, version);
        var registration = string.Join(" ", new[] { doctor.RegistrationCouncil, doctor.RegistrationNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var signed = certificate.Sign(userId, doctor.Id, doctor.DisplayName, registration.Length == 0 ? null : registration, time.GetUtcNow());
        if (signed.IsFailure)
        {
            return signed.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DetailAsync(c, ct);
    }

    /// <summary>The case's MLC entry, its signed wound certificate (a draft is not evidence) and the patient, for the printed documents.</summary>
    public async Task<MedicoLegalRecord?> MedicoLegalAsync(EmergencyCase c, CancellationToken ct)
    {
        var mlc = await db.Set<MlcRecord>().AsNoTracking().FirstOrDefaultAsync(m => m.VisitId == c.VisitId, ct);
        if (mlc is null)
        {
            return null;
        }

        var wound = await db.Set<WoundCertificate>().AsNoTracking().FirstOrDefaultAsync(w => w.MlcRecordId == mlc.Id, ct);
        var patient = await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == c.PatientId, ct);
        var doctor = wound?.SignedByPractitionerId is { } signer ? await PrintQueries.PrescriberAsync(db, signer, ct) : null;
        return new MedicoLegalRecord(c, mlc, wound, patient, doctor);
    }

    private static Error BayTaken(string code, Guid? caseId) =>
        Error.Conflict("er.bay_taken", $"Bay {code} has a patient in it. Move them first, or choose another bay.", new { caseId });

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

        // LAMA (maker-checker, slice 8): who signed, the doctor who explained the risks, and a witness who confirms with their
        // own credentials at this screen; the confirmations are spent with this save.
        AgainstAdviceRecord? declaration = null;
        if (request.Disposition == EmergencyDisposition.Lama && request.AgainstAdvice is { } asked && c.IsOpen)
        {
            var recorded = await againstAdvice.RecordAsync(asked, AgainstAdviceDeclarations.ErWitness.Key, c.PatientId, nameof(EmergencyCase), c.Id, ct);
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }

            declaration = recorded.Value;
        }

        var disposed = c.Dispose(request.Disposition, request.Notes, request.ReferredTo, Me, now, declaration);
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

        // A child is under 18 on the day of the incident (or of the case, when that is not known). A date of birth under 18
        // makes the patient a child whatever is said; without one, the doctor says, for a sexual assault, as the POCSO Act
        // protects a child.
        var dateOfBirth = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == c.PatientId).Select(p => p.DateOfBirth).FirstOrDefaultAsync(ct);
        if (request.Cause == MlcCause.SexualAssault && dateOfBirth is null && request.ChildVictim is null)
        {
            return Error.Field("childVictim", "The patient's date of birth is not known: say whether they are under 18, as the POCSO Act protects a child.");
        }

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

        var incidentDay = DateOnly.FromDateTime((request.IncidentAt ?? mlc.IdentifiedAt).ToOffset(Ist).DateTime);
        var isChild = (dateOfBirth is { } dob && dob.AddYears(18) > incidentDay) || request.ChildVictim == true;
        var described = mlc.Describe(
            request.BroughtBy, request.BroughtByName, request.Cause, request.IncidentAt, request.IncidentPlace, request.Injuries, request.ConsciousOnArrival,
            request.SmellOfAlcohol, request.PoliceStation, isChild, now);
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

        if (request.Cause == MlcCause.SexualAssault)
        {
            // Nothing may make a victim's identity known (BNS 2023, s.72): the record is kept in the sexual-violence category.
            await guard.FlagAsync(c.PatientId, SensitiveCategory.SexualViolence, $"a sexual-assault MLC ({mlc.Number})", ct);
        }

        // The victim of a sexual offence or an acid attack is treated free of cost (BNSS 2023, s.397): the visit, and the
        // visits that go on from it, are billed at nothing. A corrected cause bills them as usual again.
        await FreeTreatment.MarkAsync(db, mlc, ct);

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
        var intimated = mlc.Intimate(request.IntimatedAt, request.Mode, request.ReceivedBy, request.PoliceEntryNumber, request.ReportedTo, time.GetUtcNow());
        if (intimated.IsFailure)
        {
            return intimated.Error!;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(c, ct);
        return await DetailAsync(c, ct);
    }

    /// <summary>
    /// Records the medical examination of a victim of sexual assault by a registered medical practitioner in the
    /// practitioner master, whose name, registration and gender come from it (BNSS 2023, s.184; for a child, POCSO s.27).
    /// </summary>
    public async Task<Result<EmergencyCaseDetail>> RecordExaminationAsync(Guid id, MlcExaminationRequest request, CancellationToken ct)
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
        var examiner = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => p.Id == request.ExaminerId && p.IsActive && Doctors.Contains(p.Kind))
            .Select(p => new { p.DisplayName, p.RegistrationCouncil, p.RegistrationNumber, p.Gender })
            .FirstOrDefaultAsync(ct);
        if (examiner is null)
        {
            return Error.Field("examinerId", "The examination is by a registered medical practitioner: choose the doctor from the practitioner list.");
        }

        var patientGender = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == c.PatientId).Select(p => p.Gender).FirstAsync(ct);
        var registration = string.Join(" ", new[] { examiner.RegistrationCouncil, examiner.RegistrationNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var recorded = mlc.RecordExamination(
            new SexualAssaultExamination(
                request.ExaminerId, examiner.DisplayName, registration.Length == 0 ? null : registration, examiner.Gender, request.StartedAt, request.EndedAt,
                request.ConsentGivenBy, request.AttendantKind, request.AttendantName),
            patientGender, time.GetUtcNow());
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(c, ct);
        return await DetailAsync(c, ct);
    }

    /// <summary>Records that the examination's report went to the investigating officer (BNSS s.184(6), within seven days).</summary>
    public async Task<Result<EmergencyCaseDetail>> ForwardReportAsync(Guid id, MlcReportRequest request, CancellationToken ct)
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
        var forwarded = mlc.ForwardReport(request.ForwardedAt, request.InvestigatingOfficer, time.GetUtcNow());
        if (forwarded.IsFailure)
        {
            return forwarded.Error!;
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
        return entries.Select(m =>
        {
            var person = people[m.PatientId];
            return new MlcRegisterRow(
                m.Id, cases.GetValueOrDefault(m.VisitId), m.Number, m.IdentifiedAt, person.Name, person.Uhid, m.Cause, m.PoliceStation, m.IntimatedAt,
                m.ReceivedBy, m.IntimationOverdue(now), m.DoctorName, m.IsPocso, m.ReportDueAt, m.ReportForwardedAt, m.ReportOverdue(now),
                person.Gender is { } gender && m.MissingSafeguards(gender).Count > 0);
        }).ToList();
    }

    public async Task<(MlcRecord Entry, Patient Patient)?> MlcAsync(EmergencyCase c, CancellationToken ct)
    {
        var mlc = await db.Set<MlcRecord>().AsNoTracking().FirstOrDefaultAsync(m => m.VisitId == c.VisitId, ct);
        return mlc is null ? null : (mlc, await db.Set<Patient>().AsNoTracking().FirstAsync(p => p.Id == c.PatientId, ct));
    }

    /// <summary>India Standard Time, in which the hospital's days (and a patient's birthdays) begin.</summary>
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static readonly PractitionerKind[] Doctors = [.. PractitionerKinds.Doctors];

    private string Me => currentUser.DisplayName ?? currentUser.UserName ?? "Staff";

    private Task PublishAsync(EmergencyCase c, CancellationToken ct) => realtime.ToFacilityAsync(c.FacilityId, ChangedEvent, new { CaseId = c.Id }, ct);

    private sealed record Person(string Name, string Uhid, Gender? Gender, int? AgeYears);

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
            return new Person(hidden ? PatientNames.Restricted(p.FullName) : p.FullName, hidden ? Restricted : p.Uhid, p.Gender, age);
        });
    }

    private async Task<int?> AgeInDaysAsync(Guid patientId, CancellationToken ct)
    {
        var dateOfBirth = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => p.DateOfBirth).FirstOrDefaultAsync(ct);
        return dateOfBirth is { } dob ? time.Today().DayNumber - dob.DayNumber : null;
    }

    /// <summary>The instant an Indian calendar day begins.</summary>
    private static DateTimeOffset StartOfDay(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), Ist);

    private static int Minutes(DateTimeOffset from, DateTimeOffset to) => (int)Math.Max(0, (to - from).TotalMinutes);

    private static EsiAnswers Answers(TriageRequest r) =>
        new(r.NeedsLifeSavingIntervention, r.HighRiskSituation, r.ConfusedLethargicOrDisoriented, r.SevereDistress, r.ExpectedResources);

    private static TriageVitals Vitals(TriageRequest r) =>
        new(r.Pulse, r.RespiratoryRate, r.SpO2, r.SystolicBp, r.DiastolicBp, r.TemperatureCelsius, r.Gcs, r.PainScore);

    private static WoundCertificateDto Dto(WoundCertificate w) => new(
        w.Id, w.ExaminedAt, w.IdentificationMarks, w.History, w.Injuries, w.Remarks, w.IsSigned, w.SignedAt, w.DoctorName, w.DoctorRegistration, w.Version);

    private static EsiTriageDto Dto(EsiTriage t) => new(
        t.Id, t.AssessedAt, t.AssessedByName, t.ChiefComplaint, t.ComputedLevel, t.Level, t.Reasons.Split('\n', StringSplitOptions.RemoveEmptyEntries),
        t.OverrideReason, t.Pulse, t.RespiratoryRate, t.SpO2, t.SystolicBp, t.DiastolicBp, t.TemperatureCelsius, t.Gcs, t.PainScore);

    private MlcDto Dto(MlcRecord m, Gender? patientGender)
    {
        var now = time.GetUtcNow();
        var examination = m is { ExaminerId: { } examinerId, ExaminationStartedAt: { } started, ExaminationEndedAt: { } ended }
            ? new MlcExaminationDto(examinerId, m.ExaminerName ?? string.Empty, m.ExaminerRegistration, started, ended, m.ConsentGivenBy ?? string.Empty,
                m.AttendantKind, m.AttendantName, patientGender is { } gender ? m.MissingSafeguards(gender) : [])
            : null;
        return new(
            m.Id, m.Number, m.IdentifiedAt, m.BroughtBy, m.BroughtByName, m.Cause, m.IncidentAt, m.IncidentPlace, m.Injuries, m.ConsciousOnArrival,
            m.SmellOfAlcohol, m.DoctorName, m.DoctorRegistrationNumber, m.PoliceStation, m.IntimatedAt, m.IntimationMode, m.ReceivedBy, m.PoliceEntryNumber,
            m.IntimationOverdue(now), m.Version, m.IsChild, m.IsPocso, m.ReportedTo, examination, m.InvestigatingOfficer, m.ReportForwardedAt, m.ReportDueAt,
            m.ReportOverdue(now));
    }
}

/// <summary>What the medico-legal documents print: the case, its MLC entry, its wound certificate (if any), the patient and the doctor who signed it.</summary>
public sealed record MedicoLegalRecord(EmergencyCase Case, MlcRecord Entry, WoundCertificate? Wound, Patient Patient, PrescriberInfo? Doctor);

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
