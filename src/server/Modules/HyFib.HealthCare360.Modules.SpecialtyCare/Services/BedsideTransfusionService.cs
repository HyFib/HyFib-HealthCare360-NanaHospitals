using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// The ward's side of a unit of blood: the bedside check by two staff before it is started, the observations
/// while it runs, and its end. The patient and the unit are read from the wristband and the bag and checked against
/// the issue; the second member of staff proves who they are at the starter's screen (<see cref="BedsideWitness"/>).
/// </summary>
public sealed class BedsideTransfusionService(
    HospitalDbContext db,
    IWitnessConfirmations witnesses,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>
    /// What a second member of staff witnesses at the bedside: a unit of blood checked against the patient before it is
    /// started. Whoever may start a transfusion (nurses, doctors) may witness one, never their own.
    /// </summary>
    public static readonly WitnessPurpose BedsideWitness = new("blood.bedside_check", SpecialtyCarePermissions.BloodBankTransfuse, "a unit of blood checked at the bedside");

    /// <summary>The patient's units issued to the ward, newest first, with what became of each and the consent a start would rely on.</summary>
    public async Task<Result<PatientBloodUnits>> ForPatientAsync(Guid patientId, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => new { p.Uhid, p.FullName }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        var issues = await (
                from i in db.Set<BloodIssue>().AsNoTracking()
                join r in db.Set<BloodRequest>() on i.RequestId equals r.Id
                join c in db.Set<BloodComponent>() on i.ComponentId equals c.Id
                where i.PatientId == patientId
                orderby i.IssuedAt descending
                select new { Issue = i, r.RequestNumber, r.PatientBloodGroup, r.AdmissionId, RequestedAt = r.CreatedAt, c.VolumeMl, c.ExpiresAt })
            .Take(30)
            .ToListAsync(ct);
        var crossMatchIds = issues.Where(i => i.Issue.CrossMatchId is not null).Select(i => i.Issue.CrossMatchId!.Value).ToList();
        var crossMatchedAt = await db.Set<CrossMatch>().AsNoTracking().Where(x => crossMatchIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.PerformedAt, ct);
        var issueIds = issues.Select(i => i.Issue.Id).ToList();
        var transfusions = await db.Set<Transfusion>().AsNoTracking().Include(t => t.Observations).Where(t => issueIds.Contains(t.IssueId))
            .ToDictionaryAsync(t => t.IssueId, ct);
        var consents = await ConsentsAsync(patientId, ct);
        var admittedAt = await AdmittedAtAsync(issues.Where(i => i.AdmissionId is not null).Select(i => i.AdmissionId!.Value), ct);

        var units = issues.Select(i => new WardBloodUnit(
                i.Issue.Id, i.RequestNumber, i.Issue.UnitNumber, i.Issue.ComponentType, i.Issue.UnitBloodGroup, i.VolumeMl, i.ExpiresAt,
                i.Issue.IsEmergencyRelease,
                i.Issue.CrossMatchId is { } x && crossMatchedAt.TryGetValue(x, out var at) ? at + CrossMatch.Validity : null,
                i.Issue.IssuedAt, i.Issue.IssuedToName, i.Issue.IssuedAt + BedsideCheck.OutOfStorageLimit, i.Issue.ReturnedAt,
                ConsentFor(consents, BedsideCheck.ConsentSince(i.RequestedAt, i.AdmissionId is { } a ? admittedAt.GetValueOrDefault(a) : null))?.CapturedAt,
                transfusions.TryGetValue(i.Issue.Id, out var t) ? ToDto(t) : null))
            .ToList();
        return new PatientBloodUnits(patientId, patient.Uhid, patient.FullName, issues.FirstOrDefault()?.PatientBloodGroup ?? BloodGroup.Unknown, units);
    }

    /// <summary>
    /// The bedside check and the start. Refused, before anything is saved, for another patient's wristband or another bag
    /// (each audited as a near miss), a unit no longer usable, or no consent; the witness's confirmation is spent in the
    /// same save as the start.
    /// </summary>
    public async Task<Result<TransfusionDto>> StartAsync(Guid issueId, StartTransfusionRequest request, CancellationToken ct)
    {
        var issue = await db.Set<BloodIssue>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == issueId, ct);
        if (issue is null)
        {
            return Error.NotFound(nameof(BloodIssue), issueId);
        }

        if (issue.IsReturned)
        {
            return Error.Rule("bloodbank.already_returned", "This unit was returned to the blood bank.");
        }

        if (await db.Set<Transfusion>().AnyAsync(t => t.IssueId == issueId, ct))
        {
            return Error.Conflict("bloodbank.transfusion_started", $"Unit {issue.UnitNumber} has already been started.");
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == issue.PatientId).Select(p => new { p.Uhid }).FirstAsync(ct);
        var identity = BedsideCheck.Identity(patient.Uhid, request.WristbandUhid, issue.UnitNumber, request.BagUnitNumber);
        if (identity.IsFailure)
        {
            // A near miss: the hospital learns of it even though nothing was started.
            await audit.WriteAsync(new AuditEntry(AuditCategories.Clinical, "bloodbank.bedside_mismatch",
                $"Bedside check of unit {issue.UnitNumber} refused ({identity.Error!.Code}): wristband {Fit(request.WristbandUhid)}, bag {Fit(request.BagUnitNumber)}")
            {
                EntityType = nameof(BloodIssue),
                EntityId = issue.Id.ToString(),
                PatientId = issue.PatientId,
                Severity = AuditSeverity.Warning,
            }, ct);
            return identity.Error!;
        }

        var now = time.GetUtcNow();
        var unit = await db.Set<BloodComponent>().AsNoTracking().Where(c => c.Id == issue.ComponentId).Select(c => new { c.ExpiresAt }).FirstAsync(ct);
        var crossMatch = issue.CrossMatchId is { } crossMatchId
            ? await db.Set<CrossMatch>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == crossMatchId, ct)
            : null;
        var usable = BedsideCheck.Usable(now, issue.IssuedAt, unit.ExpiresAt, issue.IsEmergencyRelease, crossMatch);
        if (usable.IsFailure)
        {
            return usable.Error!;
        }

        var bloodRequest = await db.Set<BloodRequest>().AsNoTracking().Where(r => r.Id == issue.RequestId)
            .Select(r => new { r.AdmissionId, r.CreatedAt }).FirstAsync(ct);
        var admittedAt = bloodRequest.AdmissionId is { } admissionId ? (await AdmittedAtAsync([admissionId], ct)).GetValueOrDefault(admissionId) : null;
        var consent = ConsentFor(await ConsentsAsync(issue.PatientId, ct), BedsideCheck.ConsentSince(bloodRequest.CreatedAt, admittedAt));
        var consented = BedsideCheck.Consent(consent is not null, issue.IsEmergencyRelease, request.ConsentDeferredReason);
        if (consented.IsFailure)
        {
            return consented.Error!;
        }

        var userName = currentUser.DisplayName ?? currentUser.UserName ?? string.Empty;
        var transfusion = new Transfusion
        {
            FacilityId = issue.FacilityId,
            IssueId = issue.Id,
            RequestId = issue.RequestId,
            PatientId = issue.PatientId,
            UnitNumber = issue.UnitNumber,
            ComponentType = issue.ComponentType,
            StartedAt = now,
            StartedByUserId = currentUser.UserId,
            StartedByName = userName,
            ConsentId = consent?.Id,
            ConsentDeferredReason = consent is null ? request.ConsentDeferredReason!.Trim() : null,
            Status = TransfusionStatus.InProgress,
        };
        var witness = await witnesses.UseAsync(request.WitnessConfirmationId, BedsideWitness.Key, issue.PatientId, nameof(Transfusion), transfusion.Id, ct);
        if (witness.IsFailure)
        {
            return witness.Error!;
        }

        (transfusion.WitnessUserId, transfusion.WitnessName) = (witness.Value.UserId, witness.Value.Name);
        transfusion.Observations.Add(Observation(transfusion.Id, TransfusionObservationKind.Baseline, now, request.Baseline, userName));
        db.Add(transfusion);
        audit.Add(new AuditEntry(AuditCategories.Clinical, "bloodbank.transfusion_started",
            $"Unit {issue.UnitNumber} checked at the bedside with {witness.Value.Name} and started")
        {
            EntityType = nameof(Transfusion),
            EntityId = transfusion.Id.ToString(),
            PatientId = issue.PatientId,
            Details = new { issue = issue.Id, witness = witness.Value.UserId, consent = consent?.Id },
        });
        if (consent is null)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "bloodbank.consent_deferred",
                Fit($"Unit {issue.UnitNumber}, released in an emergency, started before the patient's consent was taken: {transfusion.ConsentDeferredReason}", 1000))
            {
                EntityType = nameof(Transfusion),
                EntityId = transfusion.Id.ToString(),
                PatientId = issue.PatientId,
                Severity = AuditSeverity.Warning,
            });
        }

        await db.SaveChangesAsync(ct);
        return ToDto(transfusion);
    }

    /// <summary>A set of observations while the transfusion runs.</summary>
    public async Task<Result<TransfusionDto>> ObserveAsync(Guid transfusionId, RecordTransfusionObservationRequest request, CancellationToken ct)
    {
        var transfusion = await db.Set<Transfusion>().Include(t => t.Observations).FirstOrDefaultAsync(t => t.Id == transfusionId, ct);
        if (transfusion is null)
        {
            return Error.NotFound(nameof(Transfusion), transfusionId);
        }

        db.SetExpectedVersion(transfusion, request.Version);
        var observation = Observation(transfusion.Id, request.Kind, request.RecordedAt ?? time.GetUtcNow(), request.Values,
            currentUser.DisplayName ?? currentUser.UserName ?? string.Empty);
        var observed = transfusion.Observe(observation);
        if (observed.IsFailure)
        {
            return observed.Error!;
        }

        db.Add(observation);
        transfusion.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDto(transfusion);
    }

    /// <summary>Ends the transfusion, completed or stopped, with the volume given and the observations at the end.</summary>
    public async Task<Result<TransfusionDto>> EndAsync(Guid transfusionId, EndTransfusionRequest request, CancellationToken ct)
    {
        var transfusion = await db.Set<Transfusion>().Include(t => t.Observations).FirstOrDefaultAsync(t => t.Id == transfusionId, ct);
        if (transfusion is null)
        {
            return Error.NotFound(nameof(Transfusion), transfusionId);
        }

        db.SetExpectedVersion(transfusion, request.Version);
        var issue = await db.Set<BloodIssue>().AsNoTracking().Where(i => i.Id == transfusion.IssueId).Select(i => new { i.ComponentId }).FirstAsync(ct);
        var volume = await db.Set<BloodComponent>().AsNoTracking().Where(c => c.Id == issue.ComponentId).Select(c => c.VolumeMl).FirstAsync(ct);
        var endedAt = request.EndedAt ?? time.GetUtcNow();
        var userName = currentUser.DisplayName ?? currentUser.UserName ?? string.Empty;
        var observation = Observation(transfusion.Id, TransfusionObservationKind.End, endedAt, request.Observation, userName);
        var ended = transfusion.End(endedAt, request.Stopped, request.StopReason, request.VolumeMl, volume, observation, currentUser.UserId, userName);
        if (ended.IsFailure)
        {
            return ended.Error!;
        }

        db.Add(observation);
        audit.Add(new AuditEntry(AuditCategories.Clinical, request.Stopped ? "bloodbank.transfusion_stopped" : "bloodbank.transfusion_completed",
            Fit(request.Stopped
                ? $"Unit {transfusion.UnitNumber} stopped after {request.VolumeMl} ml: {transfusion.StopReason}"
                : $"Unit {transfusion.UnitNumber} completed, {request.VolumeMl} ml given", 1000))
        {
            EntityType = nameof(Transfusion),
            EntityId = transfusion.Id.ToString(),
            PatientId = transfusion.PatientId,
            Severity = request.Stopped ? AuditSeverity.Warning : AuditSeverity.Info,
        });
        await db.SaveChangesAsync(ct);
        return ToDto(transfusion);
    }

    /// <summary>The patient's transfusion consents in force (given and not withdrawn), newest first.</summary>
    private async Task<List<(Guid Id, DateTimeOffset CapturedAt)>> ConsentsAsync(Guid patientId, CancellationToken ct) =>
        (await db.Set<PatientConsent>().AsNoTracking()
            .Where(c => c.PatientId == patientId && c.Type == ConsentType.BloodTransfusion && c.Granted && c.RevokedAt == null)
            .OrderByDescending(c => c.CapturedAt)
            .Select(c => new { c.Id, c.CapturedAt })
            .ToListAsync(ct))
        .Select(c => (c.Id, c.CapturedAt))
        .ToList();

    private static (Guid Id, DateTimeOffset CapturedAt)? ConsentFor(List<(Guid Id, DateTimeOffset CapturedAt)> consents, DateTimeOffset since) =>
        consents.Where(c => c.CapturedAt >= since).Select(c => ((Guid Id, DateTimeOffset CapturedAt)?)c).FirstOrDefault();

    private async Task<Dictionary<Guid, DateTimeOffset?>> AdmittedAtAsync(IEnumerable<Guid> admissionIds, CancellationToken ct)
    {
        var ids = admissionIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Set<AdmissionRecord>().AsNoTracking().Where(a => ids.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => (DateTimeOffset?)a.AdmittedAt, ct);
    }

    private TransfusionObservation Observation(Guid transfusionId, TransfusionObservationKind kind, DateTimeOffset at, TransfusionObservationValues values,
        string userName) => new()
        {
            TransfusionId = transfusionId,
            Kind = kind,
            RecordedAt = at,
            RecordedByUserId = currentUser.UserId,
            RecordedByName = userName,
            TemperatureC = values.TemperatureC,
            PulsePerMinute = values.PulsePerMinute,
            SystolicMmHg = values.SystolicMmHg,
            DiastolicMmHg = values.DiastolicMmHg,
            RespiratoryRate = values.RespiratoryRate,
            SpO2Percent = values.SpO2Percent,
        };

    internal static TransfusionDto ToDto(Transfusion t) => new(
        t.Id, t.IssueId, t.StartedAt, t.StartedByName, t.WitnessName, t.ConsentId, t.ConsentDeferredReason, t.Status, t.EndedAt, t.EndedByName, t.VolumeMl,
        t.StopReason,
        [.. t.Observations.OrderBy(o => o.RecordedAt).ThenBy(o => o.Kind).Select(o => new TransfusionObservationDto(
            o.Kind, o.RecordedAt, o.RecordedByName, o.TemperatureC, o.PulsePerMinute, o.SystolicMmHg, o.DiastolicMmHg, o.RespiratoryRate, o.SpO2Percent))],
        t.Version);

    private static string Fit(string text, int length = 32) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
