using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Quality.Services;

/// <summary>
/// Incident reporting and review (IRMS-01..03): numbered reports (named or anonymous), sentinel escalation to the
/// quality team the moment a report is saved, re-grading, assignment, the review workflow and the RCA.
/// <para>
/// A sentinel or never event is closed, and re-graded so it is no longer sentinel (no root-cause analysis due), only when a
/// second person agrees (MAKER-CHECKER.md, slice 14): someone who holds <see cref="QualityPermissions.IncidentsApprove"/>
/// (the quality manager), never the one who asked nor the incident's reporter.
/// </para>
/// </summary>
public sealed class IncidentService(
    HospitalDbContext db,
    INumberSeries numbers,
    ICurrentUser currentUser,
    AnonymousReportingScope anonymous,
    INotificationService notifications,
    IRealtimePublisher realtime,
    IApprovals approvals,
    IAuditLogger audit,
    TimeProvider time)
{
    /// <summary>A sentinel or never event closed, waiting for the quality manager.</summary>
    public const string SentinelCloseKind = "quality.sentinel_close";

    /// <summary>A sentinel or never event re-graded so it is no longer sentinel, waiting for the quality manager.</summary>
    public const string SentinelDowngradeKind = "quality.sentinel_downgrade";

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

    /// <summary>
    /// Re-grades the incident and corrects its details. Re-grading a sentinel event so it is no longer sentinel (its
    /// root-cause analysis then no longer due) changes nothing yet: it waits for the quality manager, with the asker's reason,
    /// and the answer is the approval it waits for.
    /// </summary>
    public async Task<Result<ApprovalRequestDto?>> UpdateAsync(Guid id, UpdateIncidentRequest request, CancellationToken ct)
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

        if (incident.IsSentinel && !SentinelRules.IsSentinel(request.Severity, request.SentinelKind, request.FlagAsSentinel))
        {
            return await AskDowngradeAsync(incident, request, ct);
        }

        db.SetExpectedVersion(incident, request.Version);
        var wasSentinel = incident.IsSentinel;
        Apply(incident, request);
        await db.SaveChangesAsync(ct);

        if (incident.IsSentinel && !wasSentinel)
        {
            await EscalateAsync(new IncidentReceipt(incident.Id, incident.IncidentNumber, true, incident.Severity, incident.Type), incident.FacilityId, incident.Location, ct);
        }

        return (ApprovalRequestDto?)null;
    }

    /// <summary>
    /// Moves the incident along the review path. Closing a sentinel or never event is checked as any closing is (its
    /// completed root-cause analysis, its corrective actions done) and then waits for the quality manager: the answer is the
    /// approval it waits for, and the incident stays where it is until then.
    /// </summary>
    public async Task<Result<ApprovalRequestDto?>> TransitionAsync(Guid id, TransitionIncidentRequest request, CancellationToken ct)
    {
        var incident = await db.Set<Incident>().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null)
        {
            return Error.NotFound(nameof(Incident), id);
        }

        var progress = await ProgressAsync(id, ct);
        if (request.Status == IncidentStatus.Closed && incident.IsSentinel)
        {
            var check = IncidentWorkflow.CanMove(incident.Status, IncidentStatus.Closed, true, progress, request.Note);
            return check.IsFailure ? check.Error! : await AskCloseAsync(incident, request.Note!.Trim(), progress, ct);
        }

        var result = incident.MoveTo(request.Status, request.Note, progress, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        await realtime.ToFacilityAsync(incident.FacilityId, "quality.incident.changed", new { incident.Id, incident.Status }, ct);
        return (ApprovalRequestDto?)null;
    }

    /// <summary>When the incident's closing, and its re-grading out of sentinel, were asked for and still wait.</summary>
    public async Task<(DateTimeOffset? Closure, DateTimeOffset? Downgrade)> WaitingAsync(Guid id, CancellationToken ct)
    {
        var closing = await approvals.WaitingSinceAsync(SentinelCloseKind, nameof(Incident), [id], ct);
        var downgrade = await approvals.WaitingSinceAsync(SentinelDowngradeKind, nameof(Incident), [id], ct);
        return (closing.TryGetValue(id, out var c) ? c : null, downgrade.TryGetValue(id, out var d) ? d : null);
    }

    private async Task<Result<ApprovalRequestDto?>> AskCloseAsync(Incident incident, string note, IncidentProgress progress, CancellationToken ct)
    {
        List<ApprovalDetail> details =
        [
            new("Incident", $"{incident.IncidentNumber}: {incident.Type} with {incident.Severity} outcome at {incident.Location}"),
            new("Why it is sentinel", incident.SentinelReason ?? "Sentinel event"),
            new("Status", $"{StatusWords(incident.Status)} → closed"),
            new("Root-cause analysis", progress.RcaCompleted ? "Completed" : "Not completed"),
            new("Corrective actions", progress.CapaCount == 0 ? "None" : $"{progress.CapaCount}, {progress.OpenCapaCount} still open"),
            new("Closing summary", note),
            new("Reported by", incident.IsAnonymous ? "Anonymous" : incident.ReporterName ?? "Unknown"),
            new("Why it waits", "A sentinel or never event is closed only when the quality manager agrees, never the person who asks nor the one who reported it"),
        ];
        var requested = await approvals.RequestAsync(
            SentinelCloseKind,
            new ApprovalSubject(nameof(Incident), incident.Id, incident.Version, incident.FacilityId, incident.PatientId),
            new SentinelClosePayload(incident.Id, note),
            Fit($"Close sentinel event {incident.IncidentNumber} ({incident.SentinelReason ?? incident.Type.ToString()})", 300),
            details,
            note,
            ct);
        return requested.IsFailure ? requested.Error! : requested.Value;
    }

    private async Task<Result<ApprovalRequestDto?>> AskDowngradeAsync(Incident incident, UpdateIncidentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Field("reason", "Say why this is no longer a sentinel event: the quality manager decides on your reason.");
        }

        var after = SentinelRules.Reason(request.Severity, request.SentinelKind, request.FlagAsSentinel);
        List<ApprovalDetail> details =
        [
            new("Incident", $"{incident.IncidentNumber}: {incident.Type} at {incident.Location}, {StatusWords(incident.Status).ToLowerInvariant()}"),
            new("Harm grade", $"{incident.Severity} → {request.Severity}"),
            new("Type", incident.Type == request.Type ? request.Type.ToString() : $"{incident.Type} → {request.Type}"),
            new("Never event", $"{incident.SentinelKind?.ToString() ?? "None"} → {request.SentinelKind?.ToString() ?? "None"}"),
            new("Sentinel", $"Yes ({incident.SentinelReason ?? "flagged"}) → no{(after is null ? string.Empty : $" ({after})")}"),
            new("What follows", "No root-cause analysis is then due, and the incident can be closed without one, by the quality team alone"),
            new("Why it waits", "Taking an incident out of sentinel is agreed by the quality manager, never the person who asks nor the one who reported it"),
        ];
        var requested = await approvals.RequestAsync(
            SentinelDowngradeKind,
            new ApprovalSubject(nameof(Incident), incident.Id, request.Version, incident.FacilityId, incident.PatientId),
            new SentinelDowngradePayload(incident.Id, request with { Reason = request.Reason.Trim() }),
            Fit($"Re-grade sentinel event {incident.IncidentNumber} so it is no longer sentinel ({incident.Severity} → {request.Severity})", 300),
            details,
            request.Reason,
            ct);
        return requested.IsFailure ? requested.Error! : requested.Value;
    }

    /// <summary>
    /// Closes a sentinel event whose closing was approved, in the approval's transaction (the approval saves): only over the
    /// incident as it was asked (<c>approval.stale</c>), and only while its analysis is still complete and its corrective
    /// actions done. The incident's reporter does not approve it (<c>incident.reporter_approves</c>, which leaves it waiting).
    /// </summary>
    internal async Task<Result<string>> CloseApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<SentinelClosePayload>(request.Payload);
        var loaded = await LoadForApprovalAsync(asked.IncidentId, request, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var incident = loaded.Value;
        if (incident.Status == IncidentStatus.Closed)
        {
            return Stale(incident, "was closed already");
        }

        var moved = incident.MoveTo(IncidentStatus.Closed, asked.Note, await ProgressAsync(incident.Id, ct), time.GetUtcNow());
        if (moved.IsFailure)
        {
            return moved.Error!;
        }

        AuditApproved(incident, request, "quality.incident.sentinel_closed", $"Sentinel event {incident.IncidentNumber} closed: {asked.Note}");
        return $"Incident {incident.IncidentNumber} closed";
    }

    /// <summary>
    /// Re-grades a sentinel event out of sentinel once approved, in the approval's transaction: the details as asked, over the
    /// incident as it was asked (<c>approval.stale</c>).
    /// </summary>
    internal async Task<Result<string>> DowngradeApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<SentinelDowngradePayload>(request.Payload);
        var loaded = await LoadForApprovalAsync(asked.IncidentId, request, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var incident = loaded.Value;
        if (!incident.IsSentinel)
        {
            return Stale(incident, "is no longer sentinel");
        }

        var problem = await ValidateReferencesAsync(asked.Change.DepartmentId, asked.Change.InvolvedStaffUserIds, ct);
        if (problem is not null)
        {
            return problem;
        }

        var before = $"{incident.Severity}, {incident.SentinelReason ?? "sentinel"}";
        Apply(incident, asked.Change);
        AuditApproved(incident, request, "quality.incident.sentinel_downgraded",
            $"Incident {incident.IncidentNumber} re-graded out of sentinel ({before} → {incident.Severity}): {asked.Change.Reason}");
        return $"Incident {incident.IncidentNumber} is no longer a sentinel event";
    }

    private async Task<Result<Incident>> LoadForApprovalAsync(Guid incidentId, ApprovalRequest request, CancellationToken ct)
    {
        var incident = await db.Set<Incident>().FirstOrDefaultAsync(i => i.Id == incidentId, ct);
        if (incident is null)
        {
            return Error.NotFound(nameof(Incident), incidentId);
        }

        if (incident.ReporterUserId is { } reporter && reporter == currentUser.UserId)
        {
            return Error.Forbidden("incident.reporter_approves",
                "You reported this incident, so someone else approves it: another quality manager.");
        }

        if (request.SubjectVersion is { } version && incident.Version != version)
        {
            return Stale(incident, "was changed after this was asked for");
        }

        return incident;
    }

    private static Error Stale(Incident incident, string what) =>
        Error.Rule("approval.stale", $"Incident {incident.IncidentNumber} {what}, so nothing was changed. Ask again if it still should.");

    private void AuditApproved(Incident incident, ApprovalRequest request, string action, string description)
    {
        var checker = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        audit.Add(new AuditEntry(AuditCategories.Data, action, Fit($"{description} (asked for by {request.RequestedByName}, approved by {checker})", 1000))
        {
            PatientId = incident.PatientId,
            EntityType = nameof(Incident),
            EntityId = incident.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { incident.IncidentNumber, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
    }

    private static void Apply(Incident incident, UpdateIncidentRequest request)
    {
        incident.Classify(request.Type, request.Severity, request.SentinelKind, request.FlagAsSentinel);
        incident.Location = request.Location.Trim();
        incident.DepartmentId = request.DepartmentId;
        incident.PatientId = request.PatientId;
        incident.InvolvedStaffUserIds = request.InvolvedStaffUserIds?.Distinct().ToList() ?? [];
        incident.InvolvesDevice = request.InvolvesDevice;
    }

    private static string StatusWords(IncidentStatus status) => status switch
    {
        IncidentStatus.Reported => "Reported",
        IncidentStatus.UnderReview => "Under review",
        IncidentStatus.Investigation => "Investigation",
        IncidentStatus.CapaInProgress => "Corrective actions in progress",
        _ => "Closed",
    };

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

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
            InvolvesDevice = request.InvolvesDevice,
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
    internal Task EscalateAsync(IncidentReceipt incident, Guid facilityId, string location, CancellationToken ct)
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

/// <summary>What the quality team asked: close this sentinel event, with this summary.</summary>
public sealed record SentinelClosePayload(Guid IncidentId, string Note);

/// <summary>What the quality team asked: re-grade this sentinel event as given, which takes it out of sentinel.</summary>
public sealed record SentinelDowngradePayload(Guid IncidentId, UpdateIncidentRequest Change);

/// <summary>A sentinel or never event closed: the quality team asks, the quality manager decides.</summary>
internal sealed class SentinelCloseApproval(IncidentService incidents) : IApprovalAction
{
    public string Kind => IncidentService.SentinelCloseKind;

    public string Title => "Sentinel event closed";

    public string ApprovePermission => QualityPermissions.IncidentsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => incidents.CloseApprovedAsync(request, ct);
}

/// <summary>A sentinel or never event re-graded out of sentinel (no root-cause analysis due): the quality manager decides.</summary>
internal sealed class SentinelDowngradeApproval(IncidentService incidents) : IApprovalAction
{
    public string Kind => IncidentService.SentinelDowngradeKind;

    public string Title => "Sentinel event re-graded out of sentinel";

    public string ApprovePermission => QualityPermissions.IncidentsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => incidents.DowngradeApprovedAsync(request, ct);
}
