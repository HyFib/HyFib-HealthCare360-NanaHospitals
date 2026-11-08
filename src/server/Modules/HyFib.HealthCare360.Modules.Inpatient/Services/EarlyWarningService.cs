using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.EarlyWarning;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// NEWS2 escalation on the ward. When vital signs taken during a stay score 5 or more, or 3 in a single parameter, an
/// <see cref="EarlyWarningEscalation"/> is raised in the same transaction as the vital signs, and the stay's consultant and
/// the facility's nurses in charge (<see cref="InpatientPermissions.NursingInCharge"/>) are told; a clinician other than
/// whoever took them records the response.
/// </summary>
public sealed class EarlyWarningService(
    HospitalDbContext db,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    internal const string Category = "inpatient";

    /// <summary>Runs in Clinical's unit of work (before commit): stages the escalation and tells the ward; never saves.</summary>
    public async Task RaiseAsync(VitalSignsRecorded recorded, CancellationToken ct)
    {
        if (recorded.News2Total is not { } score || EarlyWarningEscalation.LevelFor(score, recorded.News2AnySingleParameterThree) is not { } level)
        {
            return;
        }

        var admission = await db.Set<Admission>().AsNoTracking()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(a => a.PatientId == recorded.PatientId
                        && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated)
                        && a.AdmittedAt <= recorded.RecordedAt)
            .Select(a => new { a.Id, a.FacilityId, a.PatientId, a.WardId, a.BedId, a.ConsultantPractitionerId })
            .FirstOrDefaultAsync(ct);
        if (admission is null)
        {
            return;
        }

        // Levels are stored as words, so "at this level or above" is spelt out rather than compared.
        var coveredBy = level == EscalationLevel.Emergency ? [EscalationLevel.Emergency] : new[] { EscalationLevel.Urgent, EscalationLevel.Emergency };
        var alreadyOpen = await db.Set<EarlyWarningEscalation>()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(e => e.AdmissionId == admission.Id && e.Status == EscalationStatus.Open && coveredBy.Contains(e.Level), ct);
        if (alreadyOpen)
        {
            return;
        }

        var escalation = new EarlyWarningEscalation
        {
            FacilityId = admission.FacilityId,
            AdmissionId = admission.Id,
            PatientId = admission.PatientId,
            WardId = admission.WardId,
            BedId = admission.BedId,
            VitalSignsId = recorded.VitalSignsId,
            VitalsRecordedAt = recorded.RecordedAt,
            VitalsRecordedBy = recorded.RecordedByUserId,
            Score = score,
            AnySingleParameterThree = recorded.News2AnySingleParameterThree,
            IsScoreComplete = recorded.News2Complete,
            Summary = recorded.News2Summary ?? $"NEWS2 {score}",
            Level = level,
            Status = EscalationStatus.Open,
            RaisedAt = time.GetUtcNow(),
            ConsultantPractitionerId = admission.ConsultantPractitionerId,
        };
        db.Add(escalation);

        var title = level == EscalationLevel.Emergency ? "NEWS2 emergency: respond now" : "NEWS2 urgent review";
        await TellWardAsync(escalation, title,
            level == EscalationLevel.Emergency
                ? "Emergency response: review at once and call the rapid response team."
                : "Urgent review by the ward's doctor.", ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Clinical, "inpatient.news2_escalated", $"{title}: {escalation.Summary}")
        {
            PatientId = admission.PatientId,
            EntityType = nameof(EarlyWarningEscalation),
            EntityId = escalation.Id.ToString(),
            Severity = level == EscalationLevel.Emergency ? AuditSeverity.Critical : AuditSeverity.Warning,
        }, ct);
    }

    /// <summary>
    /// The reminder: every escalation still open past its level's time (<see cref="EarlyWarningEscalation.ReminderAfter"/>,
    /// 30 minutes urgent, 15 emergency) is told again, once, to the stay's consultant and the nurses in charge, marked as a
    /// reminder and audited at Warning (<c>inpatient.news2_reminder</c>). One answered before its time is never reminded.
    /// Run by <see cref="EarlyWarningReminderJob"/> every 5 minutes across facilities; returns how many it reminded of.
    /// </summary>
    public async Task<int> RemindUnansweredAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var earliestDue = now - EarlyWarningEscalation.ReminderAfter(EscalationLevel.Emergency);
        var candidates = await db.Set<EarlyWarningEscalation>()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.Status == EscalationStatus.Open && e.ReminderSentAt == null && e.RaisedAt <= earliestDue)
            .OrderBy(e => e.RaisedAt)
            .Take(200)
            .ToListAsync(ct);
        var due = candidates.Where(e => e.IsReminderDue(now)).ToList();
        foreach (var escalation in due)
        {
            var waited = (int)(now - escalation.RaisedAt).TotalMinutes;
            escalation.ReminderSentAt = now;
            await TellWardAsync(escalation, $"Reminder: {escalation.Summary} unanswered for {waited} min",
                "Nobody has recorded a response yet. " + (escalation.Level == EscalationLevel.Emergency
                    ? "Emergency response: review at once and call the rapid response team."
                    : "Urgent review by the ward's doctor."), ct);
            await audit.WriteAsync(new AuditEntry(AuditCategories.Clinical, "inpatient.news2_reminder",
                $"{escalation.Summary} not answered {waited} minutes after it was raised; the ward was reminded")
            {
                PatientId = escalation.PatientId,
                EntityType = nameof(EarlyWarningEscalation),
                EntityId = escalation.Id.ToString(),
                Severity = AuditSeverity.Warning,
            }, ct);
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    /// <summary>Tells the stay's consultant and the facility's nurses in charge, by UHID, ward and bed only (never the name).</summary>
    private async Task TellWardAsync(EarlyWarningEscalation escalation, string title, string action, CancellationToken ct)
    {
        var ignore = new[] { HospitalDbContext.FacilityFilter };
        var uhid = await db.Set<Patient>().IgnoreQueryFilters(ignore).Where(p => p.Id == escalation.PatientId).Select(p => p.Uhid).FirstAsync(ct);
        var wardName = await db.Set<Ward>().IgnoreQueryFilters(ignore).Where(w => w.Id == escalation.WardId).Select(w => w.Name).FirstOrDefaultAsync(ct);
        var bedLabel = await db.Set<Bed>().IgnoreQueryFilters(ignore).Where(b => b.Id == escalation.BedId).Select(b => b.Label).FirstOrDefaultAsync(ct);
        var body = $"{uhid}, {wardName ?? "ward"} bed {bedLabel ?? "-"}: {escalation.Summary}. {action}";
        var link = $"/inpatient/admissions/{escalation.AdmissionId}";
        var severity = escalation.Level == EscalationLevel.Emergency ? "critical" : "warning";
        if (escalation.ConsultantPractitionerId is { } consultant)
        {
            var doctors = await db.Set<User>().AsNoTracking().IgnoreQueryFilters(ignore)
                .Where(u => u.PractitionerId == consultant && u.IsActive && u.Type == UserType.Staff)
                .Select(u => u.Id)
                .ToListAsync(ct);
            await notifications.NotifyUsersAsync(doctors, title, body, link, Category, severity, ct);
        }

        await notifications.NotifyPermissionHoldersAsync(InpatientPermissions.NursingInCharge, escalation.FacilityId, title, body, link, Category, severity, ct);
    }

    public async Task<Result<EscalationDto>> RespondAsync(Guid id, RespondToEscalationRequest request, CancellationToken ct)
    {
        var escalation = await db.Set<EarlyWarningEscalation>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (escalation is null)
        {
            return Error.NotFound("Escalation", id);
        }

        if (request.Version != escalation.Version)
        {
            return Error.Conflict("escalation.stale", "Someone else changed this escalation. Reload it and try again.");
        }

        var isDoctor = currentUser.UserId is { } userId
                       && await db.Set<User>().AnyAsync(u => u.Id == userId && u.PractitionerId != null, ct);
        var responded = escalation.Respond(currentUser.UserId, currentUser.DisplayName, isDoctor, request.Outcome, request.Response, time.GetUtcNow());
        if (responded.IsFailure)
        {
            return responded.Error!;
        }

        await audit.WriteAsync(new AuditEntry(AuditCategories.Clinical, "inpatient.news2_responded",
            $"Responded to {escalation.Summary} after {escalation.MinutesToRespond} min: {request.Outcome}")
        {
            PatientId = escalation.PatientId,
            EntityType = nameof(EarlyWarningEscalation),
            EntityId = escalation.Id.ToString(),
        }, ct);
        await db.SaveChangesAsync(ct);
        return (await ListAsync(e => e.Id == id, ct)).Single();
    }

    /// <summary>Escalations of a stay, newest first.</summary>
    public Task<List<EscalationDto>> ForAdmissionAsync(Guid admissionId, CancellationToken ct) => ListAsync(e => e.AdmissionId == admissionId, ct);

    /// <summary>A ward's escalations (open ones by default), newest first; the whole facility without a ward.</summary>
    public Task<List<EscalationDto>> ForWardAsync(Guid? wardId, bool openOnly, CancellationToken ct) =>
        ListAsync(e => (wardId == null || e.WardId == wardId) && (!openOnly || e.Status == EscalationStatus.Open), ct);

    private async Task<List<EscalationDto>> ListAsync(System.Linq.Expressions.Expression<Func<EarlyWarningEscalation, bool>> filter, CancellationToken ct)
    {
        var rows = await (
                from e in db.Set<EarlyWarningEscalation>().AsNoTracking().Where(filter)
                join p in db.Set<Patient>() on e.PatientId equals p.Id
                join w in db.Set<Ward>() on e.WardId equals w.Id into wards
                from w in wards.DefaultIfEmpty()
                join b in db.Set<Bed>() on e.BedId equals b.Id into beds
                from b in beds.DefaultIfEmpty()
                orderby e.RaisedAt descending
                select new { Escalation = e, p.Uhid, WardName = w != null ? w.Name : null, BedLabel = b != null ? b.Label : null })
            .Take(200)
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        return rows.Select(r => ToDto(r.Escalation, r.Uhid, r.WardName, r.BedLabel, now)).ToList();
    }

    private static EscalationDto ToDto(EarlyWarningEscalation e, string uhid, string? wardName, string? bedLabel, DateTimeOffset now) => new(
        e.Id, e.AdmissionId, e.PatientId, uhid, e.WardId, wardName, bedLabel, e.VitalSignsId, e.VitalsRecordedAt, e.Score, e.AnySingleParameterThree,
        e.IsScoreComplete, e.Summary, e.Level, e.Status, e.RaisedAt,
        e.Status == EscalationStatus.Open ? (int)Math.Max(0, (now - e.VitalsRecordedAt).TotalMinutes) : null,
        e.RespondedAt, e.RespondedByName, e.RespondedByDoctor, e.Outcome, e.Response, e.MinutesToRespond, e.Version, e.ReminderSentAt);

    /// <summary>The latest NEWS2 of each stay's patient since admission, for the bed board, the round and the ward print.</summary>
    internal async Task<Dictionary<Guid, News2Latest>> LatestAsync(IReadOnlyCollection<(Guid AdmissionId, Guid PatientId, DateTimeOffset AdmittedAt, DateOnly? DateOfBirth)> stays,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, News2Latest>();
        if (stays.Count == 0)
        {
            return result;
        }

        var patientIds = stays.Select(s => s.PatientId).Distinct().ToList();
        var since = stays.Min(s => s.AdmittedAt);
        var latest = (await db.Set<VitalSigns>().AsNoTracking()
                .Where(v => patientIds.Contains(v.PatientId) && !v.IsEnteredInError && v.RecordedAt >= since)
                .GroupBy(v => v.PatientId)
                .Select(g => g.OrderByDescending(v => v.RecordedAt).First())
                .ToListAsync(ct))
            .ToDictionary(v => v.PatientId);
        var today = IndiaClock.LocalDate(time.GetUtcNow());
        foreach (var stay in stays)
        {
            if (latest.TryGetValue(stay.PatientId, out var vitals) && vitals.RecordedAt >= stay.AdmittedAt
                && News2.Score(vitals.ToNews2Reading(), stay.DateOfBirth, today) is { } score)
            {
                result[stay.AdmissionId] = new News2Latest(score.Total, score.Response, score.IsComplete, vitals.RecordedAt);
            }
        }

        return result;
    }
}

/// <summary>The latest NEWS2 of a stay: its score, the response it calls for, whether all seven parameters were taken, and when.</summary>
public sealed record News2Latest(int Score, News2Response Response, bool IsComplete, DateTimeOffset RecordedAt);
