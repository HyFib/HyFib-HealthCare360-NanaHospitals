using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Breaches;
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
/// </summary>
public sealed class BreachService(
    HospitalDbContext db,
    INumberSeries numbers,
    INotificationService notifications,
    ISettingsService settings,
    PrivacySettings privacy,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string Series = "privacy.breach";
    public const string Format = "DPB{YY}{SEQ:4}";
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
            breaches = breaches.Where(b => EF.Functions.ILike(b.BreachNumber, pattern) || EF.Functions.ILike(b.Title, pattern));
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
            x.Breach.ReportSubmittedAt));
    }

    public async Task<BreachDetail?> DetailAsync(Guid id, CancellationToken ct)
    {
        var breach = await db.Set<Breach>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        return breach is null ? null : await ToDetailAsync(breach, ct);
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
        breach.BreachNumber = await numbers.NextAsync(Series, null, Format, NumberResetPolicy.Yearly, ct);
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
            $"Tell the Data Protection Board without delay. The detailed report is due by {LocalTime(breach.ReportDueAt)}.",
            $"/privacy/breaches/{breach.Id}", "privacy", "warning", ct);
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

    public Task<Result> CloseAsync(Guid id, CloseBreachRequest request, CancellationToken ct) =>
        ChangeAsync(id, b => b.Close(request.Note, time.GetUtcNow()), ct, "privacy.breach.closed");

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

    public async Task<Result> RemoveAffectedAsync(Guid id, Guid patientId, CancellationToken ct)
    {
        var affected = await db.Set<BreachAffectedPatient>().FirstOrDefaultAsync(a => a.BreachId == id && a.PatientId == patientId, ct);
        if (affected is null)
        {
            return Error.NotFound("Affected patient", patientId);
        }

        if (affected.NotifiedAt is not null)
        {
            return Error.Rule("privacy.breach_already_notified", "This patient has already been sent the notice, so they stay on the list.");
        }

        db.Remove(affected);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

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
            breach.MissingForReport(), breach.MissingForNotice(), breach.Version);
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
