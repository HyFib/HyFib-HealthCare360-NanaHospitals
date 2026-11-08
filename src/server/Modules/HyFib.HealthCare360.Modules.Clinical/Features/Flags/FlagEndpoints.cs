using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Persistence;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Realtime;
using Npgsql;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Flags;

/// <summary>
/// /api/v1/emr — allergies, the "no known allergies" statement and patient alerts, the flags of the persistent patient
/// banner (EHR-03). Every change is pushed on the patient's real-time topic so open banners refresh immediately.
/// </summary>
internal static class FlagEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr").WithTags("EMR");

        group.MapGet("/patients/{patientId:guid}/allergies", ListAllergies).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/patients/{patientId:guid}/allergies", RecordAllergy).RequirePermission(ClinicalPermissions.AllergiesManage).WithValidation<RecordAllergyRequest>();
        group.MapGet("/patients/{patientId:guid}/allergies/status", AllergyStatusOf).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/patients/{patientId:guid}/allergies/none", RecordNoKnownAllergies).RequirePermission(ClinicalPermissions.AllergiesManage);
        group.MapPut("/allergies/{id:guid}", UpdateAllergy).RequirePermission(ClinicalPermissions.AllergiesManage).WithValidation<UpdateAllergyRequest>();

        group.MapGet("/patients/{patientId:guid}/alerts", ListAlerts).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/patients/{patientId:guid}/alerts", RaiseAlert).RequirePermission(ClinicalPermissions.AlertsManage).WithValidation<RaiseAlertRequest>();
        group.MapPost("/alerts/{id:guid}/deactivate", DeactivateAlert).RequirePermission(ClinicalPermissions.AlertsManage).WithValidation<DeactivateAlertRequest>();
    }

    private static async Task<IResult> ListAllergies(Guid patientId, bool? includeInactive, ClinicalAccess access, HospitalDbContext db, PatientFlagApprovals flags,
        CancellationToken ct)
    {
        var opened = await access.OpenSafetyFactsAsync(patientId, "Viewed allergies", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var allergies = db.Set<Allergy>().AsNoTracking().Where(a => a.PatientId == patientId);
        if (includeInactive != true)
        {
            allergies = allergies.Where(a => a.Status == AllergyStatus.Active);
        }

        // Severity is stored as text, so sort by clinical weight in memory (lists are short).
        var list = await allergies.ToListAsync(ct);
        var pending = await flags.PendingAsync(PatientFlagApprovals.AllergyKind, nameof(Allergy), [.. list.Select(a => a.Id)], ct);
        return TypedResults.Ok(list.OrderByDescending(a => a.Severity).ThenBy(a => a.Allergen).Select(a => ToDto(a) with { PendingChange = pending.GetValueOrDefault(a.Id) }).ToList());
    }

    private static async Task<IResult> RecordAllergy(Guid patientId, RecordAllergyRequest request, ClinicalAccess access, HospitalDbContext db,
        ICurrentUser user, IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        // Duplicates are rejected so a later status change cannot leave an identical allergy silently active.
        var allergen = DrugNames.Normalize(request.Allergen);
        var active = await db.Set<Allergy>().Where(a => a.PatientId == patientId && a.Status == AllergyStatus.Active).Select(a => new { a.Id, a.Allergen }).ToListAsync(ct);
        if (active.FirstOrDefault(a => DrugNames.Normalize(a.Allergen) == allergen) is { } existing)
        {
            return Error.Conflict("allergy.duplicate", "This allergy is already recorded. Update the existing entry instead.", new { allergyId = existing.Id }).ToProblem();
        }

        var allergy = new Allergy
        {
            PatientId = patientId,
            Allergen = request.Allergen.Trim(),
            Type = request.Type,
            Reaction = request.Reaction?.Trim(),
            Severity = request.Severity,
            OnsetDate = request.OnsetDate,
            RecordedAt = time.GetUtcNow(),
            RecordedByUserId = user.UserId,
            Notes = request.Notes?.Trim(),
        };
        db.Add(allergy);
        await EndNoKnownAllergiesAsync(db, patientId, ct);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(Topics.Patient(patientId), "clinical.flags.changed", new { allergy.Id }, ct);
        return TypedResults.Created($"/api/v1/emr/patients/{patientId}/allergies", ToDto(allergy));
    }

    /// <summary>Recorded, "no known allergies" or not recorded: the banner claims NKA only when it was recorded (EHR-03).</summary>
    private static async Task<IResult> AllergyStatusOf(Guid patientId, ClinicalAccess access, HospitalDbContext db, CancellationToken ct)
    {
        var opened = await access.OpenSafetyFactsAsync(patientId, "Viewed allergy status", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var latestAllergy = await db.Set<Allergy>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Status == AllergyStatus.Active)
            .OrderByDescending(a => a.RecordedAt)
            .Select(a => new { a.RecordedAt, a.RecordedByUserId })
            .FirstOrDefaultAsync(ct);
        var statement = await db.Set<NoKnownAllergiesStatement>().AsNoTracking()
            .Where(s => s.PatientId == patientId)
            .OrderByDescending(s => s.RecordedAt)
            .FirstOrDefaultAsync(ct);

        var status = NoKnownAllergiesStatement.StatusOf(latestAllergy is not null, statement is not null);
        return TypedResults.Ok(status switch
        {
            AllergyRecordingStatus.Recorded => new AllergyStatusDto(status, latestAllergy!.RecordedAt,
                latestAllergy.RecordedByUserId is { } userId
                    ? await db.Set<User>().AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
                    : null),
            AllergyRecordingStatus.NoKnownAllergies => ToDto(statement!),
            _ => new AllergyStatusDto(status, null, null),
        });
    }

    /// <summary>
    /// Records that the patient has no known allergies. Refused with 409 <c>allergy.active_exist</c> while an active
    /// allergy is on the chart; repeating it (or a double submit from two desks) returns the existing statement.
    /// A concurrent allergy recorded at the same moment can leave both rows, which is safe: active allergies always
    /// win in <see cref="NoKnownAllergiesStatement.StatusOf"/>, and the next allergy change removes the statement.
    /// </summary>
    private static async Task<IResult> RecordNoKnownAllergies(Guid patientId, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var active = await db.Set<Allergy>().CountAsync(a => a.PatientId == patientId && a.Status == AllergyStatus.Active, ct);
        var permitted = NoKnownAllergiesStatement.CanRecord(active);
        if (permitted.IsFailure)
        {
            return permitted.Error!.ToProblem();
        }

        var existing = await db.Set<NoKnownAllergiesStatement>().AsNoTracking().FirstOrDefaultAsync(s => s.PatientId == patientId, ct);
        if (existing is not null)
        {
            return TypedResults.Ok(ToDto(existing));
        }

        var statement = new NoKnownAllergiesStatement
        {
            PatientId = patientId,
            RecordedAt = time.GetUtcNow(),
            RecordedByUserId = user.UserId,
            RecordedByName = user.DisplayName ?? user.UserName,
        };
        db.Add(statement);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsAlreadyRecorded(ex))
        {
            // Recorded from another desk a moment ago; the outcome is the same, so answer with that statement.
            db.ChangeTracker.Clear();
            var recorded = await db.Set<NoKnownAllergiesStatement>().AsNoTracking().FirstOrDefaultAsync(s => s.PatientId == patientId, ct);
            return recorded is null ? Error.ConcurrencyConflict.ToProblem() : TypedResults.Ok(ToDto(recorded));
        }

        await realtime.PublishAsync(Topics.Patient(patientId), "clinical.flags.changed", new { statement.Id }, ct);
        return TypedResults.Ok(ToDto(statement));
    }

    private static bool IsAlreadyRecorded(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: NoKnownAllergiesStatementConfiguration.OnePerPatientIndex };

    /// <summary>
    /// Updates an allergy. What makes it stricter (a higher severity, active again) and its reaction and notes are saved at
    /// once; taking an active allergy out of the checks or lowering its severity waits for a prescriber who did not ask
    /// (maker-checker, slice 7), and the answer is the request (202). The allergy stays as it is meanwhile.
    /// </summary>
    private static async Task<IResult> UpdateAllergy(Guid id, UpdateAllergyRequest request, ClinicalAccess access, HospitalDbContext db, PatientFlagApprovals flags,
        IRealtimePublisher realtime, CancellationToken ct)
    {
        var allergy = await db.Set<Allergy>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (allergy is null)
        {
            return Error.NotFound(nameof(Allergy), id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(allergy.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        db.SetExpectedVersion(allergy, request.Version);
        if (allergy.Status == AllergyStatus.EnteredInError)
        {
            return Error.Rule("allergy.voided", "This allergy was marked as entered in error and cannot be changed. Record a new one.").ToProblem();
        }

        var now = new AllergyStanding(allergy.Status, allergy.Severity);
        var waits = AllergyChangeRule.Waits(now, new AllergyStanding(request.Status, request.Severity));
        var reason = request.Status != AllergyStatus.Active ? request.StatusReason : request.ChangeReason;
        var ask = waits is not null;
        if (waits is not null)
        {
            var waiting = await flags.WaitingAllergyAsync(allergy.Id, ct);
            if (waiting is { } w && w.Payload.Asked != waits)
            {
                return Error.Conflict(PatientFlagApprovals.WaitingCode,
                    $"A change to this allergy, asked for by {w.RequestedBy}, is waiting for approval. Withdraw it in Approvals first, or wait for its decision.").ToProblem();
            }

            ask = waiting is null;
            if (ask && string.IsNullOrWhiteSpace(reason))
            {
                return Error.Field(request.Status != AllergyStatus.Active ? "statusReason" : "changeReason",
                    "Say why: this change goes to a prescriber for approval, and they read your reason.").ToProblem();
            }

            if (ask)
            {
                // Asked for over the allergy as the clinician saw it, even when nothing else on it changes.
                db.GuardVersion(allergy, request.Version);
            }
        }
        else
        {
            var changed = allergy.ChangeStatus(request.Status, request.StatusReason);
            if (changed.IsFailure)
            {
                return changed.Error!.ToProblem();
            }

            allergy.Severity = request.Severity;
        }

        allergy.Reaction = request.Reaction?.Trim();
        allergy.Notes = request.Notes?.Trim();
        if (allergy.IsActive)
        {
            // A re-activated allergy contradicts "no known allergies" just like a new one.
            await EndNoKnownAllergiesAsync(db, allergy.PatientId, ct);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (ask)
        {
            var asked = await flags.AskAllergyAsync(allergy, waits!, request.StatusReason, reason!, ct);
            if (asked.IsFailure)
            {
                return asked.Error!.ToProblem();
            }

            await transaction.CommitAsync(ct);
            await realtime.PublishAsync(Topics.Patient(allergy.PatientId), "clinical.flags.changed", new { allergy.Id }, ct);
            return TypedResults.Accepted($"/api/v1/approvals/{asked.Value.Id}", asked.Value);
        }

        await transaction.CommitAsync(ct);
        await realtime.PublishAsync(Topics.Patient(allergy.PatientId), "clinical.flags.changed", new { allergy.Id }, ct);
        var pending = await flags.PendingAsync(PatientFlagApprovals.AllergyKind, nameof(Allergy), [allergy.Id], ct);
        return TypedResults.Ok(ToDto(allergy) with { PendingChange = pending.GetValueOrDefault(allergy.Id) });
    }

    /// <summary>An active allergy on the chart ends any "no known allergies" statement, soft-deleted in the caller's save.</summary>
    private static async Task EndNoKnownAllergiesAsync(HospitalDbContext db, Guid patientId, CancellationToken ct) =>
        db.RemoveRange(await db.Set<NoKnownAllergiesStatement>().Where(s => s.PatientId == patientId).ToListAsync(ct));

    private static async Task<IResult> ListAlerts(Guid patientId, bool? includeInactive, ClinicalAccess access, HospitalDbContext db, PatientFlagApprovals flags,
        TimeProvider time, CancellationToken ct)
    {
        var opened = await access.OpenSafetyFactsAsync(patientId, "Viewed patient alerts", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var alerts = db.Set<PatientAlert>().AsNoTracking().Where(a => a.PatientId == patientId);
        if (includeInactive != true)
        {
            alerts = alerts.Where(a => a.IsActive);
        }

        var today = time.Today();
        var list = await alerts.ToListAsync(ct);
        var pending = await flags.PendingAsync(PatientFlagApprovals.DnrKind, nameof(PatientAlert), [.. list.Where(a => a.Type == PatientAlertType.Dnr).Select(a => a.Id)], ct);
        return TypedResults.Ok(list.OrderByDescending(a => a.Severity).ThenByDescending(a => a.EffectiveFrom)
            .Select(a => ToDto(a, today) with { PendingChange = pending.GetValueOrDefault(a.Id) }).ToList());
    }

    private static async Task<IResult> RaiseAlert(Guid patientId, RaiseAlertRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        IPermissionService permissions, IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        // DNR is the treating doctor's documented decision: only someone who signs clinical notes puts it on (slice 7).
        if (request.Type == PatientAlertType.Dnr && !await SignsNotesAsync(user, permissions, ct))
        {
            return DnrDoctorOnly.ToProblem();
        }

        var alert = new PatientAlert
        {
            PatientId = patientId,
            Type = request.Type,
            Title = string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle(request.Type) : request.Title.Trim(),
            Detail = request.Detail?.Trim(),
            Severity = request.Severity ?? PatientAlert.DefaultSeverity(request.Type),
            EffectiveFrom = time.GetUtcNow(),
            ExpiresOn = request.ExpiresOn,
            RaisedByUserId = user.UserId,
        };
        db.Add(alert);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(Topics.Patient(patientId), "clinical.flags.changed", new { alert.Id }, ct);
        return TypedResults.Created($"/api/v1/emr/patients/{patientId}/alerts", ToDto(alert, time.Today()));
    }

    /// <summary>
    /// Clears an alert at once, except DNR: taking it off overrides a documented end-of-life decision, so a doctor asks
    /// and a second doctor agrees (maker-checker, slice 7); the answer is the request (202), and the banner keeps DNR.
    /// </summary>
    private static async Task<IResult> DeactivateAlert(Guid id, DeactivateAlertRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        IPermissionService permissions, PatientFlagApprovals flags, IRealtimePublisher realtime, TimeProvider time, CancellationToken ct)
    {
        var alert = await db.Set<PatientAlert>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alert is null)
        {
            return Error.NotFound("Alert", id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(alert.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        if (alert.Type == PatientAlertType.Dnr)
        {
            if (!alert.IsActive)
            {
                return Error.Rule("alert.inactive", "This alert is already inactive.").ToProblem();
            }

            if (!await SignsNotesAsync(user, permissions, ct))
            {
                return DnrDoctorOnly.ToProblem();
            }

            return (await flags.AskDnrLiftAsync(alert, request.Reason, ct)).ToHttpResult(asked => TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked));
        }

        var result = alert.Deactivate(request.Reason, user.UserId, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(Topics.Patient(alert.PatientId), "clinical.flags.changed", new { alert.Id }, ct);
        return TypedResults.Ok(ToDto(alert, time.Today()));
    }

    private static readonly Error DnrDoctorOnly =
        Error.Forbidden("alert.dnr_doctor_only", "Only a doctor puts Do Not Resuscitate on or takes it off: it records the treating doctor's decision.");

    private static async Task<bool> SignsNotesAsync(ICurrentUser user, IPermissionService permissions, CancellationToken ct) =>
        user.UserId is { } userId && await permissions.HasPermissionAsync(userId, ClinicalPermissions.EncountersSign, ct);

    private static string DefaultTitle(PatientAlertType type) => type switch
    {
        PatientAlertType.Dnr => "Do not resuscitate (DNR)",
        PatientAlertType.FallRisk => "Fall risk",
        PatientAlertType.Isolation => "Isolation precautions",
        PatientAlertType.InfectionControl => "Infection control",
        PatientAlertType.Pregnancy => "Pregnant",
        _ => "Alert",
    };

    internal static AllergyDto ToDto(Allergy a) => new(
        a.Id, a.PatientId, a.Allergen, a.Type, a.Reaction, a.Severity, a.Status, a.StatusReason, a.OnsetDate, a.RecordedAt, a.RecordedByUserId, a.Notes, a.Version);

    internal static AllergyStatusDto ToDto(NoKnownAllergiesStatement s) => new(AllergyRecordingStatus.NoKnownAllergies, s.RecordedAt, s.RecordedByName);

    internal static PatientAlertDto ToDto(PatientAlert a, DateOnly today) => new(
        a.Id, a.PatientId, a.Type, a.Title, a.Detail, a.Severity, a.EffectiveFrom, a.ExpiresOn, a.IsActive, a.IsCurrent(today), a.DeactivatedAt,
        a.DeactivationReason);
}
