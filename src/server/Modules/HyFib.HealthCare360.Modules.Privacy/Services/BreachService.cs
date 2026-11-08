using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Breaches;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>
/// A breach notice as a patient sees it in the portal: what rule 7(1) of the DPDP Rules 2025 asks each notice to say.
/// <see cref="PatientIds"/> are the people it concerns among those the portal user may act for.
/// </summary>
public sealed record PortalBreachNotice(
    Guid Id,
    IReadOnlyList<Guid> PatientIds,
    string Reference,
    string Title,
    DateTimeOffset NotifiedAt,
    DateTimeOffset AwareAt,
    DateTimeOffset? OccurredFrom,
    DateTimeOffset? OccurredTo,
    string Description,
    string? Location,
    IReadOnlyList<BreachDataCategory> DataCategories,
    string? Consequences,
    string? Mitigation,
    string? PrincipalSteps,
    PrivacyContact Contact);

/// <summary>
/// The personal-data breach register (DPDP Act 2023 s8(6); DPDP Rules 2025, rule 7): recording, the Board's 72-hour
/// clock, the people affected, and their notices by SMS, e-mail and the patient portal. The register is group-wide.
/// <para>
/// Closing a breach, which needs its detailed report to the Board recorded, and taking patients off its notification list
/// wait for a second person who holds <see cref="PrivacyPermissions.BreachesApprove"/> (the Data Protection Officer), never
/// the one who asked (MAKER-CHECKER.md, slice 14).
/// </para>
/// </summary>
public sealed class BreachService(
    HospitalDbContext db,
    INumberSeries numbers,
    INotificationService notifications,
    ISettingsService settings,
    PrivacySettings privacy,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IApprovals approvals,
    TimeProvider time)
{
    /// <summary>A breach closed, waiting for the Data Protection Officer.</summary>
    public const string CloseKind = "privacy.breach_close";

    /// <summary>Patients taken off a breach's notification list, waiting for the Data Protection Officer.</summary>
    public const string RemoveAffectedKind = "privacy.breach_affected_remove";

    /// <summary>The most patients one request takes off the list.</summary>
    public const int MaxRemovedAtOnce = 500;

    public const string NoticeTemplate = "privacy.breach.notice";
    private const int Batch = 500;

    public async Task<PagedResult<BreachListItem>> ListAsync(bool openOnly, PageRequest page, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var breaches = db.Set<Breach>().AsNoTracking();
        if (openOnly)
        {
            breaches = breaches.Where(b => b.Status != BreachStatus.Closed);
        }

        if (page.SearchTerm is { } term)
        {
            var pattern = term.ToLikePattern();
            breaches = breaches.Where(b => EF.Functions.ILike(b.BreachNumber, pattern, QueryExtensions.LikeEscape) || EF.Functions.ILike(b.Title, pattern, QueryExtensions.LikeEscape));
        }

        var rows = await breaches
            .OrderBy(b => b.Status == BreachStatus.Closed)
            .ThenByDescending(b => b.AwareAt)
            .Select(b => new
            {
                Breach = b,
                Listed = db.Set<BreachAffectedPatient>().Count(a => a.BreachId == b.Id),
                Notified = db.Set<BreachAffectedPatient>().Count(a => a.BreachId == b.Id && a.NotifiedAt != null),
            })
            .ToPagedResultAsync(page, ct);
        return rows.Map(x => new BreachListItem(x.Breach.Id, x.Breach.BreachNumber, x.Breach.Title, x.Breach.Status, x.Breach.AwareAt,
            x.Breach.ReportDueAt, x.Breach.IsReportOverdue(now), x.Breach.EstimatedAffected, x.Listed, x.Notified, x.Breach.BoardInformedAt,
            x.Breach.ReportSubmittedAt, x.Breach.CertInType, x.Breach.CertInReportDueAt, x.Breach.CertInReportedAt, x.Breach.IsCertInReportOverdue(now)));
    }

    public async Task<BreachDetail?> DetailAsync(Guid id, CancellationToken ct)
    {
        var breach = await db.Set<Breach>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (breach is null)
        {
            return null;
        }

        var waiting = await WaitingAsync(id, ct);
        return await ToDetailAsync(breach, ct) with { ClosureAskedAt = waiting.Closure, RemovalAskedAt = waiting.Removal };
    }

    public async Task<Result<BreachCreated>> CreateAsync(SaveBreachRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (request.AwareAt > now)
        {
            return Error.Field("awareAt", "The time the hospital became aware cannot be in the future.");
        }

        var breach = new Breach();
        Apply(breach, request);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        breach.BreachNumber = await numbers.NextAsync(PrivacyNumberSeries.Breach, null, ct);
        db.Add(breach);
        audit.Add(new AuditEntry(AuditCategories.Security, "privacy.breach.recorded", $"Personal data breach {breach.BreachNumber} recorded: {breach.Title}")
        {
            EntityType = nameof(Breach),
            EntityId = breach.Id.ToString(),
            Severity = AuditSeverity.Critical,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await notifications.NotifyPermissionHoldersAsync(PrivacyPermissions.BreachesManage, null,
            $"Personal data breach {breach.BreachNumber} recorded",
            breach.CertInReportDueAt is { } certInDue
                ? $"Report it to CERT-In by {LocalTime(certInDue)} (6 hours), and tell the Data Protection Board without delay. The detailed report is due by {LocalTime(breach.ReportDueAt)}."
                : $"Tell the Data Protection Board without delay. The detailed report is due by {LocalTime(breach.ReportDueAt)}.",
            $"/privacy/breaches/{breach.Id}", "privacy", breach.CertInType is null ? "warning" : "critical", ct);
        return new BreachCreated(breach.Id, breach.BreachNumber);
    }

    public async Task<Result> UpdateAsync(Guid id, SaveBreachRequest request, CancellationToken ct)
    {
        if (request.Version is not { } version)
        {
            return Error.Field("version", "Reload the breach and try again.");
        }

        return await ChangeAsync(id, breach =>
        {
            if (breach.Status == BreachStatus.Closed)
            {
                return Error.Rule("privacy.breach_closed", "A closed breach cannot be changed.");
            }

            if (request.AwareAt > time.GetUtcNow())
            {
                return Error.Field("awareAt", "The time the hospital became aware cannot be in the future.");
            }

            db.SetExpectedVersion(breach, version);
            Apply(breach, request);
            return Result.Success();
        }, ct);
    }

    public Task<Result> RecordBoardInformedAsync(Guid id, BoardInformedRequest request, CancellationToken ct) =>
        ChangeAsync(id, b => b.RecordBoardInformed(request.At, request.Reference, time.GetUtcNow()), ct, "privacy.breach.board_informed");

    public Task<Result> RecordExtensionAsync(Guid id, BoardExtensionRequest request, CancellationToken ct) =>
        ChangeAsync(id, b => b.RecordExtension(request.Until, request.Reference), ct, "privacy.breach.extended");

    public Task<Result> RecordReportSubmittedAsync(Guid id, ReportSubmittedRequest request, CancellationToken ct) =>
        ChangeAsync(id, b => b.RecordReportSubmitted(request.At, request.Reference, time.GetUtcNow()), ct, "privacy.breach.reported");

    public Task<Result> RecordCertInReportedAsync(Guid id, CertInReportedRequest request, CancellationToken ct) =>
        ChangeAsync(id, b => b.RecordCertInReported(request.At, request.Reference, time.GetUtcNow()), ct, "privacy.breach.cert_in_reported");

    /// <summary>
    /// Asks for the breach to be closed: refused until its detailed report to the Board is recorded
    /// (<c>privacy.breach_board_report_required</c>), then waiting for the Data Protection Officer; nothing changes until then.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> CloseAsync(Guid id, CloseBreachRequest request, CancellationToken ct)
    {
        var breach = await db.Set<Breach>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (breach is null)
        {
            return Error.NotFound(nameof(Breach), id);
        }

        var check = breach.CanClose();
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var affected = db.Set<BreachAffectedPatient>().AsNoTracking().Where(a => a.BreachId == id);
        var listed = await affected.CountAsync(ct);
        var notNotified = await affected.CountAsync(a => a.NotifiedAt == null, ct);
        var note = Clean(request.Note);
        List<ApprovalDetail> details =
        [
            new("Breach", $"{breach.BreachNumber}: {breach.Title}"),
            new("Status", "Detailed report sent → closed"),
            new("Board report", $"{breach.BoardReference}, sent {LocalTime(breach.ReportSubmittedAt!.Value)}"),
            new("People affected", listed == 0 ? "None listed" : $"{listed} listed, {notNotified} not yet sent the notice"),
            new("Closing note", note ?? "None"),
            new("What follows", "The breach can no longer be changed, and no more notices are sent from it"),
            new("Why it waits", "A personal data breach is closed only when the Data Protection Officer agrees, never the person who asks"),
        ];
        return await approvals.RequestAsync(
            CloseKind,
            await SubjectAsync(breach, ct),
            new BreachClosePayload(breach.Id, note),
            $"Close personal data breach {breach.BreachNumber} ({breach.Title})",
            details,
            note ?? $"Board report {breach.BoardReference} recorded",
            ct);
    }

    /// <summary>
    /// Closes a breach whose closing was approved, in the approval's transaction (the approval saves): only over the
    /// breach as it was asked (<c>approval.stale</c>) and while its Board report is still recorded.
    /// </summary>
    internal async Task<Result<string>> CloseApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<BreachClosePayload>(request.Payload);
        var breach = await db.Set<Breach>().FirstOrDefaultAsync(b => b.Id == asked.BreachId, ct);
        if (breach is null)
        {
            return Error.NotFound(nameof(Breach), asked.BreachId);
        }

        if (breach.Status == BreachStatus.Closed || (request.SubjectVersion is { } version && breach.Version != version))
        {
            return Error.Rule("approval.stale", $"Breach {breach.BreachNumber} was {(breach.Status == BreachStatus.Closed ? "closed already" : "changed after this was asked for")}, so nothing was changed. Ask again if it should still close.");
        }

        var closed = breach.Close(asked.Note, time.GetUtcNow());
        if (closed.IsFailure)
        {
            return closed.Error!;
        }

        audit.Add(new AuditEntry(AuditCategories.Security, "privacy.breach.closed",
            Fit($"Breach {breach.BreachNumber} closed on Board report {breach.BoardReference}{(asked.Note is null ? string.Empty : $": {asked.Note}")} (asked for by {request.RequestedByName}, approved by {Checker()})", 1000))
        {
            EntityType = nameof(Breach),
            EntityId = breach.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { breach.BreachNumber, breach.BoardReference, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        return $"Breach {breach.BreachNumber} closed";
    }

    public async Task<PagedResult<AffectedPatientItem>> AffectedAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        var rows = await (
                from a in db.Set<BreachAffectedPatient>().AsNoTracking()
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                where a.BreachId == id
                orderby a.NotifiedAt != null, p.FullName
                select new { a, p.Uhid, p.FullName, p.Phone, HasEmail = p.Email != null })
            .ToPagedResultAsync(page, ct);
        return rows.Map(x => new AffectedPatientItem(x.a.PatientId, x.Uhid, x.FullName, x.Phone is null ? null : Phone.Mask(x.Phone), x.HasEmail,
            x.a.AddedAt, x.a.NotifiedAt, x.a.NoticeChannels));
    }

    /// <summary>
    /// Adds patients to the breach, by id or by pasted UHIDs. A merged record counts as the patient it was merged into.
    /// Patients already listed are skipped; unknown UHIDs are returned so they can be checked.
    /// </summary>
    public async Task<Result<AddAffectedResult>> AddAffectedAsync(Guid id, AddAffectedRequest request, CancellationToken ct)
    {
        var breach = await db.Set<Breach>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (breach is null)
        {
            return Error.NotFound(nameof(Breach), id);
        }

        if (breach.Status == BreachStatus.Closed)
        {
            return Error.Rule("privacy.breach_closed", "A closed breach cannot be changed.");
        }

        var uhids = (request.Uhids ?? []).Select(u => u.Trim().ToUpperInvariant()).Where(u => u.Length > 0).Distinct().ToList();
        var byUhid = new Dictionary<string, (Guid Id, Guid? MergedInto)>(StringComparer.Ordinal);
        foreach (var chunk in uhids.Chunk(Batch))
        {
            var found = await db.Set<Patient>().AsNoTracking()
                .Where(p => chunk.Contains(p.Uhid))
                .Select(p => new { p.Uhid, p.Id, p.MergedIntoPatientId })
                .ToListAsync(ct);
            foreach (var patient in found)
            {
                byUhid[patient.Uhid.ToUpperInvariant()] = (patient.Id, patient.MergedIntoPatientId);
            }
        }

        var requestedIds = (request.PatientIds ?? []).Distinct().ToList();
        var knownIds = new List<(Guid Id, Guid? MergedInto)>();
        foreach (var chunk in requestedIds.Chunk(Batch))
        {
            knownIds.AddRange((await db.Set<Patient>().AsNoTracking()
                .Where(p => chunk.Contains(p.Id))
                .Select(p => new { p.Id, p.MergedIntoPatientId })
                .ToListAsync(ct)).Select(p => (p.Id, p.MergedIntoPatientId)));
        }

        var notFound = uhids.Where(u => !byUhid.ContainsKey(u)).ToList();
        notFound.AddRange(requestedIds.Where(i => knownIds.All(k => k.Id != i)).Select(i => i.ToString()));
        var patientIds = byUhid.Values.Concat(knownIds).Select(p => p.MergedInto ?? p.Id).Distinct().ToList();

        var listed = new HashSet<Guid>();
        foreach (var chunk in patientIds.Chunk(Batch))
        {
            listed.UnionWith(await db.Set<BreachAffectedPatient>()
                .Where(a => a.BreachId == id && chunk.Contains(a.PatientId))
                .Select(a => a.PatientId)
                .ToListAsync(ct));
        }

        var now = time.GetUtcNow();
        var added = 0;
        foreach (var patientId in patientIds.Where(p => !listed.Contains(p)))
        {
            db.Add(new BreachAffectedPatient { BreachId = id, PatientId = patientId, AddedAt = now, AddedByUserId = currentUser.UserId });
            added++;
        }

        await db.SaveChangesAsync(ct);
        return new AddAffectedResult(added, listed.Count, notFound);
    }

    /// <summary>
    /// Asks for patients to be taken off the breach's notification list, with why. A patient already sent the notice stays
    /// on it (<c>privacy.breach_already_notified</c>). Nothing changes until the Data Protection Officer approves: the
    /// patients stay listed, and a notice sent meanwhile reaches them.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> RemoveAffectedAsync(Guid id, RemoveAffectedRequest request, CancellationToken ct)
    {
        var breach = await db.Set<Breach>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (breach is null)
        {
            return Error.NotFound(nameof(Breach), id);
        }

        if (breach.Status == BreachStatus.Closed)
        {
            return Error.Rule("privacy.breach_closed", "A closed breach cannot be changed.");
        }

        var ids = request.PatientIds.Distinct().ToList();
        var rows = await (
                from a in db.Set<BreachAffectedPatient>().AsNoTracking()
                join p in db.Set<Patient>() on a.PatientId equals p.Id
                where a.BreachId == id && ids.Contains(a.PatientId)
                select new { a.PatientId, a.NotifiedAt, p.Uhid, p.FullName })
            .ToListAsync(ct);
        if (rows.Count != ids.Count)
        {
            return Error.NotFound("Affected patient", ids.First(i => rows.All(r => r.PatientId != i)));
        }

        if (rows.FirstOrDefault(r => r.NotifiedAt is not null) is { } notified)
        {
            return Error.Rule("privacy.breach_already_notified", $"{notified.FullName} ({notified.Uhid}) has already been sent the notice, so they stay on the list.");
        }

        var reason = request.Reason.Trim();
        var names = rows.OrderBy(r => r.Uhid, StringComparer.Ordinal).Select(r => $"{r.FullName} ({r.Uhid})").ToList();
        List<ApprovalDetail> details =
        [
            new("Breach", $"{breach.BreachNumber}: {breach.Title}"),
            new("Taken off the list", Fit(string.Join(", ", names), 1000)),
            new("How many", rows.Count == 1 ? "1 patient" : $"{rows.Count} patients"),
            new("What follows", "They are not sent the breach notice and do not see it in the portal"),
            new("Why it waits", "Taking people off a breach's notification list is agreed by the Data Protection Officer, never the person who asks"),
        ];
        return await approvals.RequestAsync(
            RemoveAffectedKind,
            await SubjectAsync(breach, ct),
            new BreachRemoveAffectedPayload(breach.Id, [.. rows.Select(r => r.PatientId)], reason),
            rows.Count == 1
                ? $"Take {names[0]} off the notification list of breach {breach.BreachNumber}"
                : $"Take {rows.Count} patients off the notification list of breach {breach.BreachNumber}",
            details,
            reason,
            ct);
    }

    /// <summary>
    /// Takes the patients off the list once approved, in the approval's transaction: only while every one of them is still
    /// listed and not yet sent the notice (<c>approval.stale</c>), audited with each patient removed and why.
    /// </summary>
    internal async Task<Result<string>> RemoveAffectedApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<BreachRemoveAffectedPayload>(request.Payload);
        var breach = await db.Set<Breach>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == asked.BreachId, ct);
        if (breach is null)
        {
            return Error.NotFound(nameof(Breach), asked.BreachId);
        }

        var rows = await db.Set<BreachAffectedPatient>().Where(a => a.BreachId == breach.Id && asked.PatientIds.Contains(a.PatientId)).ToListAsync(ct);
        if (breach.Status == BreachStatus.Closed || rows.Count != asked.PatientIds.Count || rows.Any(r => r.NotifiedAt is not null))
        {
            return Error.Rule("approval.stale",
                $"Breach {breach.BreachNumber}'s list changed after this was asked for (a patient was sent the notice or taken off already, or the breach closed), so nobody was taken off. Ask again.");
        }

        var patients = await db.Set<Patient>().AsNoTracking().Where(p => asked.PatientIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Uhid }).ToListAsync(ct);
        db.RemoveRange(rows);
        var uhids = patients.Select(p => p.Uhid).Order(StringComparer.Ordinal).ToList();
        audit.Add(new AuditEntry(AuditCategories.Security, "privacy.breach.affected_removed",
            Fit($"Breach {breach.BreachNumber}: {rows.Count} taken off the notification list ({string.Join(", ", uhids)}): {asked.Reason} (asked for by {request.RequestedByName}, approved by {Checker()})", 1000))
        {
            EntityType = nameof(Breach),
            EntityId = breach.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { breach.BreachNumber, patientIds = asked.PatientIds, uhids, reason = asked.Reason, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        foreach (var patient in patients)
        {
            audit.Add(new AuditEntry(AuditCategories.Data, "privacy.breach.affected_removed",
                Fit($"Taken off the notification list of breach {breach.BreachNumber}: {asked.Reason}", 1000))
            {
                PatientId = patient.Id,
                EntityType = nameof(Breach),
                EntityId = breach.Id.ToString(),
            });
        }

        return rows.Count == 1 ? $"1 patient taken off breach {breach.BreachNumber}'s list" : $"{rows.Count} patients taken off breach {breach.BreachNumber}'s list";
    }

    /// <summary>When the breach's closing and a removal from its list were asked for and still wait.</summary>
    public async Task<(DateTimeOffset? Closure, DateTimeOffset? Removal)> WaitingAsync(Guid id, CancellationToken ct)
    {
        var closing = await approvals.WaitingSinceAsync(CloseKind, nameof(Breach), [id], ct);
        var removal = await approvals.WaitingSinceAsync(RemoveAffectedKind, nameof(Breach), [id], ct);
        return (closing.TryGetValue(id, out var c) ? c : null, removal.TryGetValue(id, out var r) ? r : null);
    }

    /// <summary>The register is group-wide: a request is asked in the asker's facility (the first facility when none is chosen).</summary>
    private async Task<ApprovalSubject> SubjectAsync(Breach breach, CancellationToken ct)
    {
        var facility = currentUser.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        return new ApprovalSubject(nameof(Breach), breach.Id, breach.Version, facility, null);
    }

    private string Checker() => currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    /// <summary>
    /// Sends the notice to every listed patient who has not had it: by SMS (the short notice and a link to the full one
    /// in the portal) and by e-mail (the full notice). Patients with neither are counted, so the hospital can reach them
    /// another way; they still see the notice when they sign in to the portal.
    /// </summary>
    public async Task<Result<NotifyAffectedResult>> NotifyAsync(Guid id, CancellationToken ct)
    {
        var breach = await db.Set<Breach>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (breach is null)
        {
            return Error.NotFound(nameof(Breach), id);
        }

        if (breach.Status == BreachStatus.Closed)
        {
            return Error.Rule("privacy.breach_closed", "A closed breach cannot send notices.");
        }

        var missing = breach.MissingForNotice();
        if (missing.Count > 0)
        {
            return Error.Rule("privacy.breach_notice_incomplete", $"Fill in {string.Join(", ", missing)} before sending the notice.");
        }

        var now = time.GetUtcNow();
        var already = await db.Set<BreachAffectedPatient>().CountAsync(a => a.BreachId == id && a.NotifiedAt != null, ct);
        var contact = PrivacySettings.ContactLine(await privacy.PublishedContactAsync(ct));
        var portal = await PortalAddress.BaseUrlAsync(settings, null, ct);
        var values = NoticeValues(breach, contact, portal is null ? "the patient portal" : PortalAddress.Page(portal, $"my-privacy/notices/{breach.Id}"));
        var queued = 0;
        var withoutContact = 0;

        // Walk the list in id order: patients without contact details are looked at again on every run, so a number
        // added since gets the notice.
        var after = Guid.Empty;
        while (true)
        {
            var cursor = after;
            var pending = await (
                    from a in db.Set<BreachAffectedPatient>()
                    join p in db.Set<Patient>() on a.PatientId equals p.Id
                    where a.BreachId == id && a.NotifiedAt == null && a.Id.CompareTo(cursor) > 0
                    orderby a.Id
                    select new { Affected = a, p.Phone, p.Email, p.FullName })
                .Take(Batch)
                .ToListAsync(ct);
            if (pending.Count == 0)
            {
                break;
            }

            after = pending[^1].Affected.Id;

            foreach (var row in pending)
            {
                var channels = new List<NotificationChannel>();
                if (row.Phone is not null)
                {
                    channels.Add(NotificationChannel.Sms);
                }

                if (row.Email is not null)
                {
                    channels.Add(NotificationChannel.Email);
                }

                if (channels.Count == 0)
                {
                    row.Affected.NoticeChannels = "none";
                    withoutContact++;
                    continue;
                }

                notifications.Queue(NoticeTemplate, new NotificationRecipient(row.Phone, row.Email, row.Affected.PatientId, row.FullName),
                    values, channels);
                row.Affected.NotifiedAt = now;
                row.Affected.NoticeChannels = string.Join(",", channels).ToLowerInvariant();
                queued++;
            }

            if (queued + withoutContact > 0)
            {
                breach.NotifiedAt ??= now;
            }

            await db.SaveChangesAsync(ct);
        }

        audit.Add(new AuditEntry(AuditCategories.Security, "privacy.breach.notified",
            string.Create(CultureInfo.InvariantCulture, $"Breach {breach.BreachNumber}: notices queued for {queued}, {withoutContact} without contact details"))
        {
            EntityType = nameof(Breach),
            EntityId = breach.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return new NotifyAffectedResult(queued, withoutContact, already);
    }

    public async Task<BreachReport?> ReportAsync(Guid id, CancellationToken ct)
    {
        var detail = await DetailAsync(id, ct);
        if (detail is null)
        {
            return null;
        }

        var hospital = await db.Set<Facility>().AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.CreatedAt)
            .Select(f => new { f.Name, f.LegalName, f.Address, f.RegistrationNumber })
            .FirstOrDefaultAsync(ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "privacy.breach.report_viewed", $"Board report for breach {detail.BreachNumber} prepared")
        {
            EntityType = nameof(Breach),
            EntityId = id.ToString(),
        }, ct);
        return new BreachReport(hospital?.Name ?? "Hospital", hospital?.LegalName,
            hospital?.Address.ToString() is { Length: > 0 } address ? address : null, hospital?.RegistrationNumber,
            await privacy.PublishedContactAsync(ct), detail, time.GetUtcNow());
    }

    /// <summary>The notices a portal user's patients were sent, newest first.</summary>
    public async Task<IReadOnlyList<PortalBreachNotice>> NoticesForAsync(IReadOnlyCollection<Guid> patientIds, Guid? noticeId, CancellationToken ct)
    {
        var breaches = await (
                from b in db.Set<Breach>().AsNoTracking()
                where b.NotifiedAt != null
                      && (noticeId == null || b.Id == noticeId)
                      && db.Set<BreachAffectedPatient>().Any(a => a.BreachId == b.Id && patientIds.Contains(a.PatientId))
                orderby b.NotifiedAt descending
                select b)
            .ToListAsync(ct);
        if (breaches.Count == 0)
        {
            return [];
        }

        var breachIds = breaches.Select(b => b.Id).ToList();
        var affected = await db.Set<BreachAffectedPatient>().AsNoTracking()
            .Where(a => breachIds.Contains(a.BreachId) && patientIds.Contains(a.PatientId))
            .Select(a => new { a.BreachId, a.PatientId })
            .ToListAsync(ct);
        var contact = await privacy.PublishedContactAsync(ct);
        return [.. breaches.Select(b => new PortalBreachNotice(b.Id, [.. affected.Where(a => a.BreachId == b.Id).Select(a => a.PatientId)],
            b.BreachNumber, b.Title, b.NotifiedAt!.Value, b.AwareAt, b.OccurredFrom, b.OccurredTo, b.Description, b.Location,
            Parse<BreachDataCategory>(b.DataCategories), b.Consequences, b.Mitigation, b.PrincipalSteps, contact))];
    }

    private async Task<Result> ChangeAsync(Guid id, Func<Breach, Result> change, CancellationToken ct, string? auditAction = null)
    {
        var breach = await db.Set<Breach>().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (breach is null)
        {
            return Error.NotFound(nameof(Breach), id);
        }

        var result = change(breach);
        if (result.IsFailure)
        {
            return result;
        }

        if (auditAction is not null)
        {
            audit.Add(new AuditEntry(AuditCategories.Security, auditAction, $"Breach {breach.BreachNumber}: {breach.Status}")
            {
                EntityType = nameof(Breach),
                EntityId = breach.Id.ToString(),
            });
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<BreachDetail> ToDetailAsync(Breach breach, CancellationToken ct)
    {
        var affected = db.Set<BreachAffectedPatient>().AsNoTracking().Where(a => a.BreachId == breach.Id);
        var listed = await affected.CountAsync(ct);
        var notified = await affected.CountAsync(a => a.NotifiedAt != null, ct);
        var withoutContact = await affected.CountAsync(a => a.NotifiedAt == null && a.NoticeChannels == "none", ct);
        return new BreachDetail(
            breach.Id, breach.BreachNumber, breach.Title, breach.Description, breach.Status, breach.AwareAt, breach.OccurredFrom, breach.OccurredTo,
            Parse<BreachNature>(breach.Nature), breach.Location, Parse<BreachDataCategory>(breach.DataCategories), breach.EstimatedAffected,
            breach.Consequences, breach.Mitigation, breach.PrincipalSteps, breach.Cause, breach.Findings, breach.Remediation, breach.PrincipalNotice,
            breach.BoardInformedAt,
            breach.ReportDueAt, breach.ExtendedUntil, breach.ReportSubmittedAt, breach.BoardReference, breach.ClosedAt, breach.ClosingNote, breach.NotifiedAt,
            breach.IsReportOverdue(time.GetUtcNow()), listed, notified, withoutContact,
            breach.MissingForReport(), breach.MissingForNotice(), breach.Version, breach.CertInType, breach.CertInReportDueAt, breach.CertInReportedAt,
            breach.CertInReference, breach.IsCertInReportOverdue(time.GetUtcNow()));
    }

    private static void Apply(Breach breach, SaveBreachRequest request)
    {
        breach.Title = request.Title.Trim();
        breach.Description = request.Description.Trim();
        breach.AwareAt = request.AwareAt;
        breach.OccurredFrom = request.OccurredFrom;
        breach.OccurredTo = request.OccurredTo;
        breach.Nature = [.. (request.Nature ?? []).Distinct().Select(n => n.ToString())];
        breach.Location = Clean(request.Location);
        breach.DataCategories = [.. (request.DataCategories ?? []).Distinct().Select(c => c.ToString())];
        breach.EstimatedAffected = request.EstimatedAffected;
        breach.Consequences = Clean(request.Consequences);
        breach.Mitigation = Clean(request.Mitigation);
        breach.PrincipalSteps = Clean(request.PrincipalSteps);
        breach.Cause = Clean(request.Cause);
        breach.Findings = Clean(request.Findings);
        breach.Remediation = Clean(request.Remediation);
        breach.PrincipalNotice = Clean(request.PrincipalNotice);

        // Once CERT-In has been told, the kind of incident it was told of stays.
        if (breach.CertInReportedAt is null)
        {
            breach.CertInType = request.CertInType;
        }
    }

    /// <summary>The template values for the notice: the SMS uses the short notice and the link; the e-mail carries it all.</summary>
    internal static Dictionary<string, string> NoticeValues(Breach breach, string contact, string link) => new()
    {
        ["reference"] = breach.BreachNumber,
        ["summary"] = breach.PrincipalNotice ?? string.Empty,
        ["link"] = link,
        ["description"] = breach.Description,
        ["when"] = breach.OccurredFrom is { } from
            ? breach.OccurredTo is { } to && to != from ? $"{LocalTime(from)} to {LocalTime(to)}" : LocalTime(from)
            : $"Found on {LocalTime(breach.AwareAt)}",
        ["data"] = breach.DataCategories.Count == 0 ? "Being established" : string.Join(", ", breach.DataCategories.Select(CategoryWords)),
        ["consequences"] = breach.Consequences ?? string.Empty,
        ["mitigation"] = breach.Mitigation ?? string.Empty,
        ["steps"] = breach.PrincipalSteps ?? string.Empty,
        ["contact"] = contact,
    };

    private static string CategoryWords(string category) => category switch
    {
        nameof(BreachDataCategory.Identity) => "name and identity details",
        nameof(BreachDataCategory.Contact) => "contact details",
        nameof(BreachDataCategory.Health) => "health information",
        nameof(BreachDataCategory.Financial) => "bills and payment details",
        nameof(BreachDataCategory.GovernmentId) => "government ID numbers",
        nameof(BreachDataCategory.Credentials) => "sign-in details",
        nameof(BreachDataCategory.Children) => "children's data",
        _ => category,
    };

    private static List<T> Parse<T>(IEnumerable<string> values)
        where T : struct, Enum =>
        [.. values.Select(v => Enum.TryParse<T>(v, out var parsed) ? parsed : (T?)null).OfType<T>()];

    private static string LocalTime(DateTimeOffset instant) =>
        instant.ToOffset(TimeSpan.FromMinutes(330)).ToString("dd MMM yyyy HH:mm 'IST'", CultureInfo.InvariantCulture);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What was asked: close this breach, with this note.</summary>
public sealed record BreachClosePayload(Guid BreachId, string? Note);

/// <summary>What was asked: take these patients off this breach's notification list, for this reason.</summary>
public sealed record BreachRemoveAffectedPayload(Guid BreachId, IReadOnlyList<Guid> PatientIds, string Reason);

/// <summary>A personal data breach closed: the breach team asks, the Data Protection Officer decides.</summary>
internal sealed class BreachCloseApproval(BreachService breaches) : IApprovalAction
{
    public string Kind => BreachService.CloseKind;

    public string Title => "Personal data breach closed";

    public string ApprovePermission => PrivacyPermissions.BreachesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => breaches.CloseApprovedAsync(request, ct);
}

/// <summary>Patients taken off a breach's notification list: the breach team asks, the Data Protection Officer decides.</summary>
internal sealed class BreachRemoveAffectedApproval(BreachService breaches) : IApprovalAction
{
    public string Kind => BreachService.RemoveAffectedKind;

    public string Title => "Patients taken off a breach's notification list";

    public string ApprovePermission => PrivacyPermissions.BreachesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => breaches.RemoveAffectedApprovedAsync(request, ct);
}
