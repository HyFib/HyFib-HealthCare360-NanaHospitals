using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain.Audits;
using HyFib.HealthCare360.Modules.Quality.Features.Audits;
using HyFib.HealthCare360.Modules.Quality.Features.Capas;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>
/// NABH internal audits: checklists by chapter, the schedule (a recurring audit schedules its next one when completed), the
/// findings recorded by the lead auditor or the quality team, a CAPA in the CAPA register for each non-compliance, and
/// closure once every CAPA is verified effective — which the CAPA register lets only someone other than its owner do.
/// </summary>
public sealed class AuditService(
    HospitalDbContext db,
    INumberSeries numbers,
    ICurrentUser currentUser,
    IPermissionService permissions,
    INotificationService notifications,
    CapaService capas,
    TimeProvider time)
{
    // ------------------------------------------------------------------ templates

    public async Task<IReadOnlyList<AuditTemplateDto>> TemplatesAsync(NabhChapter? chapter, bool includeInactive, CancellationToken ct) =>
        (await db.Set<AuditTemplate>().AsNoTracking()
            .Where(t => (includeInactive || t.IsActive) && (chapter == null || t.Chapter == chapter))
            .OrderBy(t => t.Chapter).ThenBy(t => t.Code)
            .ToListAsync(ct))
        .Select(Dto)
        .ToList();

    public async Task<Result<AuditTemplateDto>> SaveTemplateAsync(Guid? id, SaveAuditTemplateRequest request, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<AuditTemplate>().AnyAsync(t => t.Code == code && t.Id != id, ct))
        {
            return Error.Conflict("audit.template_code_in_use", $"A checklist {code} already exists.");
        }

        AuditTemplate template;
        if (id is { } existing)
        {
            var found = await db.Set<AuditTemplate>().FirstOrDefaultAsync(t => t.Id == existing, ct);
            if (found is null)
            {
                return Error.NotFound("Audit checklist", existing);
            }

            template = found;
            db.SetExpectedVersion(template, request.Version);
        }
        else
        {
            template = new AuditTemplate();
            db.Add(template);
        }

        var items = new List<AuditItem>();
        foreach (var (item, index) in request.Items.Select((item, index) => (item, index)))
        {
            var key = string.IsNullOrWhiteSpace(item.Key) ? $"I{index + 1:00}" : item.Key.Trim();
            if (items.Any(i => i.Key == key))
            {
                return Error.Field("items", $"Two items have the key {key}.");
            }

            items.Add(new AuditItem(key, item.Text.Trim(), string.IsNullOrWhiteSpace(item.Reference) ? null : item.Reference.Trim()));
        }

        template.Code = code;
        template.Title = request.Title.Trim();
        template.Chapter = request.Chapter;
        template.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        template.Items = items;
        template.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return Dto(template);
    }

    // ------------------------------------------------------------------ audits

    public async Task<IReadOnlyList<AuditListItem>> ListAsync(AuditStatus? status, NabhChapter? chapter, bool mine, CancellationToken ct)
    {
        var today = time.LocalToday();
        var userId = currentUser.UserId;
        var audits = await db.Set<QualityAudit>().AsNoTracking().Include(a => a.Findings)
            .Where(a => (status == null ? a.Status != AuditStatus.Cancelled : a.Status == status) && (chapter == null || a.Chapter == chapter)
                        && (!mine || a.LeadAuditorUserId == userId))
            .OrderBy(a => a.ScheduledOn)
            .Take(500)
            .ToListAsync(ct);
        var names = await NamesAsync(audits.Select(a => a.LeadAuditorUserId), ct);
        var capaIds = audits.SelectMany(a => a.Findings).Where(f => f.CapaId != null).Select(f => f.CapaId!.Value).ToList();
        var capaStatus = await db.Set<Capa>().AsNoTracking().Where(c => capaIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Status, ct);
        return audits.Select(a => new AuditListItem(a.Id, a.AuditNumber, a.Title, a.Chapter, a.Area, a.ScheduledOn, a.LeadAuditorUserId, names.GetValueOrDefault(a.LeadAuditorUserId),
                a.Recurrence, a.Status, a.Status is AuditStatus.Scheduled or AuditStatus.InProgress && a.ScheduledOn < today, a.CompliancePercent(),
                a.Findings.Count(f => f.Result == FindingResult.NonCompliant),
                a.Findings.Count(f => f.CapaId is { } c && capaStatus.TryGetValue(c, out var s) && s is not (CapaStatus.Verified or CapaStatus.Cancelled))))
            .ToList();
    }

    public async Task<Result<AuditDetail>> GetAsync(Guid id, CancellationToken ct)
    {
        var audit = await Load(id, tracking: false, ct);
        if (audit is null)
        {
            return Error.NotFound("Audit", id);
        }

        return await MayRecordAsync(audit, ct)
            ? await DetailAsync(audit, ct)
            : Error.Forbidden("audit.not_auditor", "Only the lead auditor or the quality team opens this audit.");
    }

    public async Task<Result<AuditDetail>> ScheduleAsync(ScheduleAuditRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var template = await db.Set<AuditTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.IsActive, ct);
        if (template is null)
        {
            return Error.Field("templateId", "Choose an active checklist.");
        }

        if (!await db.Set<User>().AnyAsync(u => u.Id == request.LeadAuditorUserId && u.IsActive && u.Type == UserType.Staff, ct))
        {
            return Error.Field("leadAuditorUserId", "Choose an active staff member as lead auditor.");
        }

        var audit = await NewAuditAsync(facilityId, template, request.ScheduledOn, request.LeadAuditorUserId, request.DepartmentId, request.Area, request.Recurrence, ct);
        await db.SaveChangesAsync(ct);
        if (audit.LeadAuditorUserId != currentUser.UserId)
        {
            await notifications.NotifyUsersAsync([audit.LeadAuditorUserId], $"Audit {audit.AuditNumber} scheduled",
                $"{audit.Title} on {audit.ScheduledOn:dd MMM yyyy}: you are the lead auditor.", $"/quality/audits?audit={audit.Id}", "quality", "info", ct);
        }

        return await DetailAsync(audit, ct);
    }

    public Task<Result<AuditDetail>> StartAsync(Guid id, AuditStepRequest request, CancellationToken ct) =>
        ChangeAsync(id, request.Version, auditorMayAct: true, (audit, now) => audit.Start(now), ct);

    public Task<Result<AuditDetail>> RecordAsync(Guid id, RecordFindingRequest request, CancellationToken ct) =>
        ChangeAsync(id, request.Version, auditorMayAct: true, (audit, now) =>
        {
            if (audit.Status == AuditStatus.Scheduled)
            {
                audit.Start(now);
            }

            return audit.Record(request.ItemKey, request.Result, request.Observation);
        }, ct);

    public async Task<Result<AuditDetail>> RaiseCapaAsync(Guid id, Guid findingId, RaiseFindingCapaRequest request, CancellationToken ct)
    {
        var audit = await Load(id, tracking: true, ct);
        if (audit is null)
        {
            return Error.NotFound("Audit", id);
        }

        db.SetExpectedVersion(audit, request.Version);
        var finding = audit.Findings.FirstOrDefault(f => f.Id == findingId);
        if (finding is null)
        {
            return Error.NotFound("Audit finding", findingId);
        }

        if (audit.Status is not (AuditStatus.Scheduled or AuditStatus.InProgress))
        {
            return Error.Rule("audit.not_open", "CAPAs are raised while the audit is open.");
        }

        if (finding.Result != FindingResult.NonCompliant)
        {
            return Error.Rule("audit.finding_compliant", "A CAPA is raised for a non-compliance.");
        }

        if (finding.CapaId is not null)
        {
            return Error.Rule("audit.finding_has_capa", "This finding already has a CAPA.");
        }

        var action = $"{audit.AuditNumber} · {finding.ItemText}: {request.Action.Trim()}";
        var created = await capas.CreateAsync(new CreateCapaRequest(CapaSource.Audit, null, null, request.Type,
            action.Length <= 2000 ? action : action[..1999] + "…", request.OwnerUserId, request.DueDate), ct);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        finding.CapaId = created.Value;
        await db.SaveChangesAsync(ct);
        return await DetailAsync(audit, ct);
    }

    public async Task<Result<AuditDetail>> CompleteAsync(Guid id, CompleteAuditRequest request, CancellationToken ct)
    {
        var audit = await Load(id, tracking: true, ct);
        if (audit is null)
        {
            return Error.NotFound("Audit", id);
        }

        if (!await MayRecordAsync(audit, ct))
        {
            return Error.Forbidden("audit.not_auditor", "Only the lead auditor or the quality team records this audit.");
        }

        db.SetExpectedVersion(audit, request.Version);
        var completed = audit.Complete(request.Summary, time.GetUtcNow());
        if (completed.IsFailure)
        {
            return completed.Error!;
        }

        if (QualityAudit.Next(audit.ScheduledOn, audit.Recurrence) is { } next)
        {
            var template = await db.Set<AuditTemplate>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == audit.TemplateId, ct);
            if (template is { IsActive: true })
            {
                var following = await NewAuditAsync(audit.FacilityId, template, next, audit.LeadAuditorUserId, audit.DepartmentId, audit.Area, audit.Recurrence, ct);
                audit.NextAuditId = following.Id;
            }
        }

        await db.SaveChangesAsync(ct);
        return await DetailAsync(audit, ct);
    }

    public async Task<Result<AuditDetail>> CloseAsync(Guid id, AuditStepRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "Sign in first.");
        }

        var audit = await Load(id, tracking: true, ct);
        if (audit is null)
        {
            return Error.NotFound("Audit", id);
        }

        db.SetExpectedVersion(audit, request.Version);
        var capaIds = audit.Findings.Where(f => f.CapaId != null).Select(f => f.CapaId!.Value).ToList();
        var statuses = await db.Set<Capa>().AsNoTracking().Where(c => capaIds.Contains(c.Id)).Select(c => c.Status).ToListAsync(ct);
        var closed = audit.Close(statuses, userId, time.GetUtcNow());
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DetailAsync(audit, ct);
    }

    public Task<Result<AuditDetail>> CancelAsync(Guid id, CancelAuditRequest request, CancellationToken ct) =>
        ChangeAsync(id, request.Version, auditorMayAct: false, (audit, _) => audit.Cancel(request.Reason), ct);

    // ------------------------------------------------------------------ helpers

    private async Task<QualityAudit> NewAuditAsync(Guid facilityId, AuditTemplate template, DateOnly on, Guid lead, Guid? departmentId, string? area, AuditRecurrence recurrence,
        CancellationToken ct)
    {
        var audit = new QualityAudit
        {
            FacilityId = facilityId,
            AuditNumber = await numbers.NextAsync(QualityNumberSeries.Audit, facilityId, ct),
            TemplateId = template.Id,
            Title = template.Title,
            Chapter = template.Chapter,
            DepartmentId = departmentId,
            Area = string.IsNullOrWhiteSpace(area) ? null : area.Trim(),
            ScheduledOn = on,
            LeadAuditorUserId = lead,
            Recurrence = recurrence,
            Status = AuditStatus.Scheduled,
        };
        audit.Findings = template.Items.Select((item, index) => new AuditFinding
        {
            AuditId = audit.Id,
            Order = index + 1,
            ItemKey = item.Key,
            ItemText = item.Text,
            Reference = item.Reference,
        }).ToList();
        db.Add(audit);
        return audit;
    }

    private async Task<Result<AuditDetail>> ChangeAsync(Guid id, uint version, bool auditorMayAct, Func<QualityAudit, DateTimeOffset, Result> change, CancellationToken ct)
    {
        var audit = await Load(id, tracking: true, ct);
        if (audit is null)
        {
            return Error.NotFound("Audit", id);
        }

        var manager = await IsManagerAsync(ct);
        if (!manager && !(auditorMayAct && audit.LeadAuditorUserId == currentUser.UserId))
        {
            return Error.Forbidden("audit.not_auditor", "Only the lead auditor or the quality team records this audit.");
        }

        db.SetExpectedVersion(audit, version);
        var result = change(audit, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await DetailAsync(audit, ct);
    }

    private async Task<bool> MayRecordAsync(QualityAudit audit, CancellationToken ct) =>
        audit.LeadAuditorUserId == currentUser.UserId || await IsManagerAsync(ct);

    private async Task<bool> IsManagerAsync(CancellationToken ct) =>
        currentUser.UserId is { } userId && await permissions.HasPermissionAsync(userId, QualityPermissions.AuditsManage, ct);

    private Task<QualityAudit?> Load(Guid id, bool tracking, CancellationToken ct)
    {
        var query = db.Set<QualityAudit>().Include(a => a.Findings).AsQueryable();
        return (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    private async Task<AuditDetail> DetailAsync(QualityAudit a, CancellationToken ct)
    {
        var capaIds = a.Findings.Where(f => f.CapaId != null).Select(f => f.CapaId!.Value).ToList();
        var capaRows = await db.Set<Capa>().AsNoTracking().Where(c => capaIds.Contains(c.Id))
            .Select(c => new { c.Id, c.CapaNumber, c.Status, c.OwnerUserId, c.DueDate })
            .ToDictionaryAsync(c => c.Id, ct);
        var names = await NamesAsync(capaRows.Values.Select(c => c.OwnerUserId).Append(a.LeadAuditorUserId), ct);
        var findings = a.Findings.OrderBy(f => f.Order).Select(f =>
        {
            var capa = f.CapaId is { } c && capaRows.TryGetValue(c, out var row) ? row : null;
            return new AuditFindingDto(f.Id, f.Order, f.ItemKey, f.ItemText, f.Reference, f.Result, f.Observation, f.CapaId, capa?.CapaNumber, capa?.Status,
                capa is null ? null : names.GetValueOrDefault(capa.OwnerUserId), capa?.DueDate);
        }).ToList();
        return new AuditDetail(a.Id, a.AuditNumber, a.TemplateId, a.Title, a.Chapter, a.DepartmentId, a.Area, a.ScheduledOn, a.LeadAuditorUserId,
            names.GetValueOrDefault(a.LeadAuditorUserId), a.Recurrence, a.Status, a.StartedAt, a.CompletedAt, a.ClosedAt, a.Summary, a.CancelReason, a.CompliancePercent(),
            a.CompletionGaps(), findings, a.NextAuditId,
            a.Status is AuditStatus.Scheduled or AuditStatus.InProgress && await MayRecordAsync(a, ct), a.Version);
    }

    private async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var ids = userIds.Distinct().ToList();
        return await db.Set<User>().AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static AuditTemplateDto Dto(AuditTemplate t) => new(t.Id, t.Code, t.Title, t.Chapter, t.Description, t.Items, t.IsActive, t.Version);
}
