using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>
/// Incident reporting and review (IRMS-01..03): numbered reports (named or anonymous), sentinel escalation to the
/// quality team the moment a report is saved, re-grading, assignment, the review workflow and the RCA.
/// </summary>
public sealed class IncidentService(
    HospitalDbContext db,
    INumberSeries numbers,
    ICurrentUser currentUser,
    AnonymousReportingScope anonymous,
    INotificationService notifications,
    IRealtimePublisher realtime,
    TimeProvider time)
{
    public async Task<Result<IncidentReceipt>> ReportAsync(ReportIncidentRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility before reporting.");
        }

        var problem = await ValidateReferencesAsync(request.DepartmentId, request.InvolvedStaffUserIds, ct);
        if (problem is not null)
        {
            return problem;
        }

        var now = time.GetUtcNow();
        var receipt = request.IsAnonymous
            ? await anonymous.RunAsync(sp => CreateAsync(sp.GetRequiredService<HospitalDbContext>(), sp.GetRequiredService<INumberSeries>(), request, facilityId, null, null, now, ct))
            : await CreateAsync(db, numbers, request, facilityId, currentUser.UserId, currentUser.DisplayName, now, ct);
        if (receipt.IsFailure)
        {
            return receipt;
        }

        await EscalateAsync(receipt.Value, facilityId, request.Location, ct);
        await realtime.ToFacilityAsync(facilityId, "quality.incident.reported", new { receipt.Value.Id }, ct);
        return receipt;
    }

    public async Task<Result> UpdateAsync(Guid id, UpdateIncidentRequest request, CancellationToken ct)
    {
        var incident = await db.Set<Incident>().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null)
        {
            return Error.NotFound(nameof(Incident), id);
        }

        var problem = await ValidateReferencesAsync(request.DepartmentId, request.InvolvedStaffUserIds, ct);
        if (problem is not null)
        {
            return problem;
        }

        db.SetExpectedVersion(incident, request.Version);
        var wasSentinel = incident.IsSentinel;
        incident.Classify(request.Type, request.Severity, request.SentinelKind, request.FlagAsSentinel);
        incident.Location = request.Location.Trim();
        incident.DepartmentId = request.DepartmentId;
        incident.PatientId = request.PatientId;
        incident.InvolvedStaffUserIds = request.InvolvedStaffUserIds?.Distinct().ToList() ?? [];
        await db.SaveChangesAsync(ct);

        if (incident.IsSentinel && !wasSentinel)
        {
            await EscalateAsync(new IncidentReceipt(incident.Id, incident.IncidentNumber, true, incident.Severity, incident.Type), incident.FacilityId, incident.Location, ct);
        }

        return Result.Success();
    }

    public async Task<Result> TransitionAsync(Guid id, TransitionIncidentRequest request, CancellationToken ct)
    {
        var incident = await db.Set<Incident>().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null)
        {
            return Error.NotFound(nameof(Incident), id);
        }

        var result = incident.MoveTo(request.Status, request.Note, await ProgressAsync(id, ct), time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        await realtime.ToFacilityAsync(incident.FacilityId, "quality.incident.changed", new { incident.Id, incident.Status }, ct);
        return Result.Success();
    }

    public async Task<Result> AssignAsync(Guid id, AssignIncidentRequest request, CancellationToken ct)
    {
        var incident = await db.Set<Incident>().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null)
        {
            return Error.NotFound(nameof(Incident), id);
        }

        if (!await db.Set<User>().AnyAsync(u => u.Id == request.UserId && u.IsActive && u.Type == UserType.Staff, ct))
        {
            return Error.Field("userId", "Choose an active staff user.");
        }

        incident.AssignedToUserId = request.UserId;
        if (incident.Status == IncidentStatus.Reported)
        {
            incident.MoveTo(IncidentStatus.UnderReview, "Assigned for review", IncidentProgress.None, time.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);
        if (request.UserId != currentUser.UserId)
        {
            await notifications.NotifyUsersAsync([request.UserId], $"Incident {incident.IncidentNumber} assigned to you",
                $"{incident.Type} ({incident.Severity}) at {incident.Location}.", $"/quality/incidents/{incident.Id}", "quality",
                incident.IsSentinel ? "critical" : "info", ct);
        }

        return Result.Success();
    }

    /// <summary>Creates or updates the RCA; the first save moves an incident under review into investigation.</summary>
    public async Task<Result<Guid>> SaveInvestigationAsync(Guid incidentId, SaveInvestigationRequest request, CancellationToken ct)
    {
        var incident = await db.Set<Incident>().FirstOrDefaultAsync(i => i.Id == incidentId, ct);
        if (incident is null)
        {
            return Error.NotFound(nameof(Incident), incidentId);
        }

        if (incident.Status == IncidentStatus.Closed)
        {
            return Error.Rule("incident.closed", "Reopen the incident before changing its analysis.");
        }

        var investigators = request.InvestigatorUserIds?.Distinct().ToList() ?? [];
        var problem = await ValidateReferencesAsync(null, investigators, ct);
        if (problem is not null)
        {
            return problem;
        }

        var investigation = await db.Set<Investigation>().Include(i => i.Factors).FirstOrDefaultAsync(i => i.IncidentId == incidentId, ct);
        if (investigation is null)
        {
            investigation = new Investigation { FacilityId = incident.FacilityId, IncidentId = incidentId, StartedAt = time.GetUtcNow() };
            db.Add(investigation);
        }
        else if (investigation.IsCompleted)
        {
            return Error.Rule("rca.completed", "The analysis is complete. Reopen it to make changes.");
        }

        // Its factors are rows of their own, so the investigation is written on every save and its version checked.
        db.GuardVersion(investigation, request.Version);

        investigation.Method = request.Method;
        investigation.ProblemStatement = request.ProblemStatement.Trim();
        investigation.Whys = request.Whys?.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).ToList() ?? [];
        investigation.Findings = Clean(request.Findings);
        investigation.Recommendations = Clean(request.Recommendations);
        investigation.InvestigatorUserIds = investigators;
        investigation.Factors.Clear();
        investigation.Factors.AddRange((request.Factors ?? []).Select(f => new RcaFactor
        {
            InvestigationId = investigation.Id,
            Category = f.Category,
            Description = f.Description.Trim(),
            IsRootCause = f.IsRootCause,
        }));

        if (incident.Status is IncidentStatus.Reported or IncidentStatus.UnderReview)
        {
            if (incident.Status == IncidentStatus.Reported)
            {
                incident.MoveTo(IncidentStatus.UnderReview, null, IncidentProgress.None, time.GetUtcNow());
            }

            incident.MoveTo(IncidentStatus.Investigation, "Root-cause analysis started", IncidentProgress.None, time.GetUtcNow());
        }

        await db.SaveChangesAsync(ct);
        return investigation.Id;
    }

    public async Task<Result> CompleteInvestigationAsync(Guid incidentId, CancellationToken ct)
    {
        var investigation = await db.Set<Investigation>().Include(i => i.Factors).FirstOrDefaultAsync(i => i.IncidentId == incidentId, ct);
        if (investigation is null)
        {
            return Error.NotFound(nameof(Investigation), incidentId);
        }

        var result = investigation.Complete(currentUser.UserId, time.GetUtcNow());
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    public async Task<Result> ReopenInvestigationAsync(Guid incidentId, CancellationToken ct)
    {
        var investigation = await db.Set<Investigation>().FirstOrDefaultAsync(i => i.IncidentId == incidentId, ct);
        if (investigation is null)
        {
            return Error.NotFound(nameof(Investigation), incidentId);
        }

        var result = investigation.Reopen();
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>RCA and CAPA facts the workflow needs for an incident.</summary>
    public async Task<IncidentProgress> ProgressAsync(Guid incidentId, CancellationToken ct)
    {
        var rcaCompleted = await db.Set<Investigation>().AnyAsync(i => i.IncidentId == incidentId && i.Status == InvestigationStatus.Completed, ct);
        var capas = await db.Set<Capa>().Where(c => c.IncidentId == incidentId && c.Status != CapaStatus.Cancelled)
            .Select(c => c.Status).ToListAsync(ct);
        return new IncidentProgress(rcaCompleted, capas.Count, capas.Count(s => s is CapaStatus.Open or CapaStatus.InProgress));
    }

    private static async Task<Result<IncidentReceipt>> CreateAsync(
        HospitalDbContext context,
        INumberSeries series,
        ReportIncidentRequest request,
        Guid facilityId,
        Guid? reporterUserId,
        string? reporterName,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var incident = new Incident
        {
            FacilityId = facilityId,
            OccurredAt = request.OccurredAt,
            ReportedAt = now,
            Location = request.Location.Trim(),
            DepartmentId = request.DepartmentId,
            PatientId = request.PatientId,
            InvolvedStaffUserIds = request.InvolvedStaffUserIds?.Distinct().ToList() ?? [],
            Description = request.Description.Trim(),
            ImmediateAction = Clean(request.ImmediateAction),
            Witnesses = Clean(request.Witnesses),
            IsAnonymous = reporterUserId is null,
            ReporterUserId = reporterUserId,
            ReporterName = reporterUserId is null ? null : reporterName,
            Status = IncidentStatus.Reported,
        };
        incident.Classify(request.Type, request.Severity, request.SentinelKind, request.FlagAsSentinel);

        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        incident.IncidentNumber = await series.NextAsync(QualityNumberSeries.Incident, facilityId, ct);
        incident.MarkReported();
        context.Add(incident);
        await context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new IncidentReceipt(incident.Id, incident.IncidentNumber, incident.IsSentinel, incident.Severity, incident.Type);
    }

    /// <summary>
    /// Sentinel events alert every quality manager immediately (critical); moderate harm is flagged for review.
    /// The message never names the reporter.
    /// </summary>
    private Task EscalateAsync(IncidentReceipt incident, Guid facilityId, string location, CancellationToken ct)
    {
        var link = $"/quality/incidents/{incident.Id}";
        if (incident.IsSentinel)
        {
            return notifications.NotifyPermissionHoldersAsync(QualityPermissions.IncidentsManage, facilityId,
                $"SENTINEL EVENT {incident.IncidentNumber}",
                $"{incident.Type} with {incident.Severity} outcome at {location}. Start the review and root-cause analysis now.",
                link, "quality", "critical", ct);
        }

        return incident.Severity == IncidentSeverity.Moderate
            ? notifications.NotifyPermissionHoldersAsync(QualityPermissions.IncidentsManage, facilityId,
                $"Incident {incident.IncidentNumber} with moderate harm",
                $"{incident.Type} at {location} needs review.", link, "quality", "warning", ct)
            : Task.CompletedTask;
    }

    private async Task<Error?> ValidateReferencesAsync(Guid? departmentId, IReadOnlyList<Guid>? staffUserIds, CancellationToken ct)
    {
        if (departmentId is { } id && !await db.Set<Department>().AnyAsync(d => d.Id == id, ct))
        {
            return Error.Field("departmentId", "Choose a department of this facility.");
        }

        if (staffUserIds is { Count: > 0 })
        {
            var ids = staffUserIds.Distinct().ToList();
            var found = await db.Set<User>().CountAsync(u => ids.Contains(u.Id) && u.Type == UserType.Staff, ct);
            if (found != ids.Count)
            {
                return Error.Field("involvedStaffUserIds", "Choose staff members from the user list.");
            }
        }

        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
