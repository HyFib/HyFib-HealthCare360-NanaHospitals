using System.Globalization;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>A message an alert went out in, as the message log has it now.</summary>
/// <param name="Recipient">The number it went to, masked as the message log keeps it.</param>
public sealed record AlertMessage(
    AlertLevel Level, NotificationChannel Channel, string? Recipient, NotificationStatus Status, DateTimeOffset QueuedAt, DateTimeOffset? SentAt,
    DateTimeOffset? DeliveredAt, string? Error);

/// <summary>An alert and when it got how far.</summary>
/// <param name="FirstDeliveredAt">When the first message to IT on-call was delivered, by the provider's delivery report.</param>
/// <param name="FirstReadAt">When the first of those who may see System health opened it in the app.</param>
/// <param name="MinutesToAcknowledge">From when it was told to when it was acknowledged.</param>
public sealed record AlertIncidentDto(
    Guid Id, string Code, string? Server, string Message, AlertLevel Level, DateTimeOffset RaisedAt, DateTimeOffset? ToldAt,
    DateTimeOffset? FirstDeliveredAt, DateTimeOffset? FirstReadAt, DateTimeOffset? SecondContactAt, DateTimeOffset? SupportAt,
    DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy, DateTimeOffset? ClearedAt, double? MinutesToAcknowledge, IReadOnlyList<AlertMessage> Messages);

/// <summary>
/// The alerts of a period, with how long they took to be acknowledged (audit P2): the open ones and those raised since
/// <see cref="From"/>, newest first.
/// </summary>
/// <param name="Unacknowledged">Open or cleared without anyone acknowledging them.</param>
/// <param name="MedianMinutesToAcknowledge">Over those acknowledged; null when none was.</param>
/// <param name="NinetiethMinutesToAcknowledge">The 90th percentile (nearest rank) over those acknowledged.</param>
public sealed record AlertReport(
    DateTimeOffset From, DateTimeOffset To, int Total, int Acknowledged, int Unacknowledged, int ReachedSecondContact, int ReachedSupport,
    double? MedianMinutesToAcknowledge, double? NinetiethMinutesToAcknowledge, IReadOnlyList<AlertIncidentDto> Incidents);

/// <summary>
/// Critical System health warnings as incidents that someone must acknowledge (audit P2). Each check of the alerts
/// (<see cref="SystemHealthAlerts"/>, every five minutes) opens an incident for each critical warning that has none, closes
/// those whose warning has gone, and takes each open one a step further (<see cref="AlertEscalation.Next"/>): told in the
/// app and to IT on-call, escalated to the second contact and then HyFib support while nobody acknowledges it, and told
/// again a day later while it lasts. Each step is decided from what the incident records, under one lock across the
/// servers, so a restart, a missed check or a second server neither repeats nor skips one. The alert of a server that had
/// stopped reporting is over when it reports again, and everyone it reached is told so, by the same messages
/// (<see cref="IsBack"/>).
/// </summary>
public sealed class AlertIncidents(
    HospitalDbContext db, IAuditLogger audit, ICurrentUser user, TimeProvider time, INotificationService notifications, ISettingsService settings)
{
    public const string AcknowledgedAction = "alerts.acknowledged";
    public const string EscalatedAction = "alerts.escalated";

    /// <summary>How many of the alerts that cleared the report lists, the latest first.</summary>
    public const int Listed = 500;

    /// <summary>
    /// Why a message of a server's alert for not reporting, not sent yet when the server reports again, is withdrawn: the
    /// alert of its return says both when it was not reporting and when it was again, so the two never arrive the wrong way
    /// round.
    /// </summary>
    internal const string WithdrawnBecauseBack =
        "Withdrawn: the server was reporting again before this alert went. The alert of its return says when it was not reporting and when it was again.";

    /// <summary>
    /// Why the alert of a server's return, not sent yet when the server stops reporting again, is withdrawn: it is no
    /// longer true, and it would reach the phone around the new alert that says so.
    /// </summary>
    internal const string WithdrawnBecauseSilentAgain =
        "Withdrawn: the server had stopped reporting again before this went. The new alert says so.";

    private const string Entity = "AlertIncident";

    /// <summary>
    /// One check against the critical warnings at <paramref name="now"/> (the alerts' own clock): opens and closes incidents,
    /// and takes the step each open one is due. Returns the warnings told in the app and to IT on-call, first or again.
    /// </summary>
    /// <param name="reporting">
    /// With several servers (SCAL-01), the names of those that report now: a server whose alert for not reporting clears is
    /// back only if it is among them. Null on a single Server Node, where none is told back.
    /// </param>
    internal async Task<IReadOnlyList<HealthWarning>> CheckAsync(
        IReadOnlyList<HealthWarning> critical, DateTimeOffset now, IReadOnlyCollection<string>? reporting, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(critical);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await TakeTurnAsync(db, ct);
        var present = critical.GroupBy(Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var open = await db.Set<AlertIncident>().Where(i => i.ClearedAt == null).ToListAsync(ct);
        var back = new List<AlertIncident>();
        foreach (var gone in open.Where(i => !present.ContainsKey(Key(i))))
        {
            gone.ClearedAt = now;
            if (IsBack(gone, reporting))
            {
                back.Add(gone);
            }
        }

        var current = new List<(AlertIncident Incident, HealthWarning Warning)>();
        foreach (var (key, warning) in present)
        {
            if (open.FirstOrDefault(i => Key(i) == key) is not { } incident)
            {
                incident = Raise(warning, now);
                await WithdrawUnsentReturnAsync(incident, ct);
            }

            current.Add((incident, warning));
        }

        var escalation = await AlertEscalation.ReadAsync(settings, ct);
        var onCall = await WifiMonitor.OnCallAsync(settings, ct);
        var undelivered = await UndeliveredAsync([.. current.Select(c => c.Incident)], ct);
        var told = new List<HealthWarning>();
        var drills = new List<(string Code, IReadOnlyList<Guid> LogIds)>();
        string? hospital = null;
        foreach (var (incident, warning) in current)
        {
            switch (escalation.Next(incident, now, undelivered.Contains(incident.Id)))
            {
                case AlertStep.Tell:
                    var sent = await TellAsync(warning, onCall, ct);
                    incident.ToldAt = now;
                    incident.OnCallMessageIds = [.. incident.OnCallMessageIds, .. sent];
                    told.Add(warning);
                    if (warning.Code.StartsWith(AlertDrills.WarningPrefix, StringComparison.Ordinal))
                    {
                        drills.Add((warning.Code, sent));
                    }

                    break;
                case AlertStep.Remind:
                    incident.RemindedAt = now;
                    incident.OnCallMessageIds = [.. incident.OnCallMessageIds, .. await TellAsync(warning, onCall, ct)];
                    told.Add(warning);
                    break;
                case AlertStep.EscalateToSecondContact:
                    incident.Level = AlertLevel.SecondContact;
                    incident.SecondContactAt = now;
                    incident.SecondContactMessageIds = [.. Page(escalation.SecondContactPhone!, "Second contact", warning.Server ?? Environment.MachineName,
                        $"Not acknowledged {Minutes(incident, now)} min after IT on-call was told: {warning.Message} Acknowledge it under Administration › System health.")];
                    Audit(incident, escalation.SecondContactPhone!, "the second contact", now);
                    break;
                case AlertStep.EscalateToSupport:
                    hospital ??= await HospitalAsync(ct);
                    incident.Level = AlertLevel.Support;
                    incident.SupportAt = now;
                    incident.SupportMessageIds = [.. Page(escalation.SupportPhone!, "HyFib support",
                        $"{hospital}, {warning.Server ?? Environment.MachineName}",
                        $"Not acknowledged at the hospital {Minutes(incident, now)} min after it was told: {warning.Message}")];
                    Audit(incident, escalation.SupportPhone!, "HyFib support", now);
                    break;
            }
        }

        // Back: everyone the alert reached is told it is over, by the same messages, queued in this transaction with the
        // alert's clearing, so that it goes once. Its messages not sent yet are withdrawn: the outbox sends what is due
        // first, and one of them tried again after a failure would reach the phone after the return.
        foreach (var incident in back)
        {
            await NotificationLogs.WithdrawAsync(db, AllMessageIds(incident), WithdrawnBecauseBack, ct);
            var message = BackMessage(incident.Server, incident.ToldAt!.Value, now);
            await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, SystemHealthAlerts.BackTitle,
                Body(message, incident.Server), "/admin/system", "system", "info", ct);
            incident.OnCallMessageIds = [.. incident.OnCallMessageIds, .. QueueOnCall(onCall, incident.Server, message)];
            if (incident.SecondContactAt is not null && escalation.SecondContactPhone is { } secondContact)
            {
                incident.SecondContactMessageIds = [.. incident.SecondContactMessageIds, .. Page(secondContact, "Second contact", incident.Server, message)];
            }

            if (incident.SupportAt is not null && escalation.SupportPhone is { } support)
            {
                hospital ??= await HospitalAsync(ct);
                incident.SupportMessageIds = [.. incident.SupportMessageIds, .. Page(support, "HyFib support", $"{hospital}, {incident.Server}", message)];
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // A drill keeps when it was told, and the messages it went out in, for its timings.
        foreach (var (code, logIds) in drills)
        {
            await AlertDrills.RecordToldAsync(db, audit, time, code, logIds, ct);
        }

        return told;
    }

    /// <summary>
    /// Acknowledges an open alert: someone is on it. Nobody further is paged; the warning stays until its condition clears.
    /// A drill's alert acknowledges the drill too.
    /// </summary>
    public async Task<Result<AlertIncidentDto>> AcknowledgeAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await TakeTurnAsync(db, ct);
        if (await db.Set<AlertIncident>().SingleOrDefaultAsync(i => i.Id == id, ct) is not { } incident)
        {
            return Error.NotFound("Alert", id);
        }

        if (incident.AcknowledgedAt is { } at)
        {
            return Error.Conflict("alerts.acknowledged",
                string.Create(CultureInfo.InvariantCulture, $"This alert was acknowledged by {incident.AcknowledgedBy ?? "someone"} at {at:dd MMM HH:mm} UTC."));
        }

        if (incident.ClearedAt is { } cleared)
        {
            return Error.Conflict("alerts.cleared",
                string.Create(CultureInfo.InvariantCulture, $"This alert cleared at {cleared:dd MMM HH:mm} UTC, before anyone acknowledged it."));
        }

        Acknowledge(audit, user, incident, time.GetUtcNow());
        if (incident.Code.StartsWith(AlertDrills.WarningPrefix, StringComparison.Ordinal))
        {
            // The drill's own acknowledgement, so its warning goes; it does not come back here.
            await new AlertDrills(db, audit, user, time).AcknowledgeOpenAsync(incident.Code[AlertDrills.WarningPrefix.Length..], ct);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (await DtosAsync([incident], ct)).Single();
    }

    /// <summary>
    /// The open alerts and those raised in the last <paramref name="days"/> days, with how long each took. The figures are
    /// over every one of them; the list holds every open one and the latest <see cref="Listed"/> that cleared.
    /// </summary>
    public async Task<AlertReport> ReportAsync(int days, CancellationToken ct)
    {
        var to = time.GetUtcNow();
        var from = to.AddDays(-Math.Clamp(days, 1, 366));
        var period = db.Set<AlertIncident>().AsNoTracking().Where(i => i.RaisedAt >= from || i.ClearedAt == null);
        var all = await period.Select(i => new { i.ToldAt, i.AcknowledgedAt, i.SecondContactAt, i.SupportAt }).ToListAsync(ct);
        var minutes = all.Where(i => i.AcknowledgedAt is not null && i.ToldAt is not null)
            .Select(i => Math.Round((i.AcknowledgedAt!.Value - i.ToldAt!.Value).TotalMinutes, 1)).Order().ToList();

        // Each open one is someone's to acknowledge, however old; there is one per warning and server at most.
        var open = await period.Where(i => i.ClearedAt == null).ToListAsync(ct);
        var cleared = await period.Where(i => i.ClearedAt != null)
            .OrderByDescending(i => i.RaisedAt).ThenByDescending(i => i.Id)
            .Take(Listed)
            .ToListAsync(ct);
        var listed = await DtosAsync([.. open.Concat(cleared).OrderByDescending(i => i.RaisedAt).ThenByDescending(i => i.Id)], ct);
        return new AlertReport(
            from, to, all.Count, all.Count(i => i.AcknowledgedAt is not null), all.Count(i => i.AcknowledgedAt is null),
            all.Count(i => i.SecondContactAt is not null), all.Count(i => i.SupportAt is not null),
            Percentile(minutes, 0.5), Percentile(minutes, 0.9), listed);
    }

    /// <summary>
    /// Acknowledges the open alert of a drill acknowledged on its own page, so the alert is not counted as one nobody
    /// acknowledged when the drill's warning goes. Saved with the caller's changes, in its transaction.
    /// </summary>
    internal static async Task AcknowledgeDrillAsync(
        HospitalDbContext db, IAuditLogger audit, ICurrentUser user, TimeProvider time, string warningCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        await TakeTurnAsync(db, ct);
        var open = await db.Set<AlertIncident>().Where(i => i.Code == warningCode && i.ClearedAt == null && i.AcknowledgedAt == null).ToListAsync(ct);
        foreach (var incident in open)
        {
            Acknowledge(audit, user, incident, time.GetUtcNow());
        }
    }

    /// <summary>
    /// Whether a message in this state has reached someone or may still: queued, sent or delivered. One that failed (its
    /// last attempt, though the outbox may try again), was skipped or was suppressed (the number opted out of WhatsApp)
    /// has not, and a critical alert does not wait on the retries.
    /// </summary>
    internal static bool Reaches(NotificationStatus status) =>
        status is not (NotificationStatus.Failed or NotificationStatus.Skipped or NotificationStatus.Suppressed);

    /// <summary>The nearest-rank percentile of <paramref name="sorted"/>; null when it is empty.</summary>
    internal static double? Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>
    /// Whether an alert that cleared was told for a server that had stopped reporting
    /// (<see cref="SystemHealthService.NotReportingCode"/>) and reports again: it is among <paramref name="reporting"/>.
    /// Its warning also goes when the server is stopped on purpose, which removes it from the servers, and when it has been
    /// silent for a day, when it is dropped from them; neither is back, and nobody is told so.
    /// </summary>
    internal static bool IsBack(AlertIncident incident, IReadOnlyCollection<string>? reporting)
    {
        ArgumentNullException.ThrowIfNull(incident);
        return incident.Code == SystemHealthService.NotReportingCode
            && incident.ToldAt is not null
            && reporting is not null
            && reporting.Any(name => string.Equals(Clip(name, AlertIncident.ServerLength), incident.Server, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What the alert of a server's return says: that it reports again, which it does only while it is on, its HC360Server
    /// service runs and it reaches the database; and when it was not reporting (its alert went) and when it was again, in
    /// the hospital's time, with the day when they differ. It says both, so it stands alone when the alert of its silence
    /// was withdrawn before it went.
    /// </summary>
    internal static string BackMessage(string server, DateTimeOffset toldAt, DateTimeOffset backAt)
    {
        var told = TimeZoneInfo.ConvertTime(toldAt, HospitalTimeZone.Zone);
        var back = TimeZoneInfo.ConvertTime(backAt, HospitalTimeZone.Zone);
        var format = told.Date == back.Date ? "HH:mm" : "d MMM HH:mm";
        return $"{server} is reporting again: it is on, its HC360Server service runs and it reaches the database. "
            + $"Not reporting at {told.ToString(format, CultureInfo.InvariantCulture)}, reporting again at {back.ToString(format, CultureInfo.InvariantCulture)}.";
    }

    private static void Acknowledge(IAuditLogger audit, ICurrentUser user, AlertIncident incident, DateTimeOffset now)
    {
        incident.AcknowledgedAt = now;
        incident.AcknowledgedById = user.UserId;
        incident.AcknowledgedBy = user.DisplayName ?? user.UserName;
        audit.Add(new AuditEntry(AuditCategories.Admin, AcknowledgedAction, Clip($"Acknowledged the alert {incident.Code}: {incident.Message}", 1000))
        {
            EntityType = Entity,
            EntityId = incident.Id.ToString(),
        });
    }

    /// <summary>
    /// A new alert for a warning that has none open: one that cleared and comes back is told again, as a new alert (P8). A
    /// broken audit chain that comes back is a second break, not the first one still going.
    /// </summary>
    private AlertIncident Raise(HealthWarning warning, DateTimeOffset now)
    {
        var raised = new AlertIncident
        {
            Code = Clip(warning.Code, AlertIncident.CodeLength),
            Server = Clip(warning.Server ?? string.Empty, AlertIncident.ServerLength),
            Message = Clip(warning.Message, AlertIncident.MessageLength),
            RaisedAt = now,
        };
        db.Add(raised);
        return raised;
    }

    /// <summary>In the app to everyone who may see System health, and to IT on-call by SMS (and WhatsApp where chosen).</summary>
    private async Task<IReadOnlyList<Guid>> TellAsync(HealthWarning warning, OnCallAlerts onCall, CancellationToken ct)
    {
        await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.SystemRead, null, SystemHealthAlerts.Title, Body(warning.Message, warning.Server),
            "/admin/system", "system", "critical", ct);
        return QueueOnCall(onCall, warning.Server ?? Environment.MachineName, warning.Message);
    }

    /// <summary>
    /// To IT on-call by SMS (and WhatsApp where chosen); nothing when no number is set. Tried through an internet outage
    /// until the reminder after it is due, which takes its place while the warning lasts: the outage ends with one message,
    /// not one for every day it lasted.
    /// </summary>
    private IReadOnlyList<Guid> QueueOnCall(OnCallAlerts onCall, string server, string message) =>
        string.IsNullOrWhiteSpace(onCall.Phone)
            ? []
            : notifications.Queue(AlertReadiness.AlertTemplate, new NotificationRecipient(Phone.Normalize(onCall.Phone), null, null, "IT on-call"),
                new Dictionary<string, string> { ["server"] = server, ["message"] = message },
                onCall.WhatsApp ? [NotificationChannel.Sms, NotificationChannel.WhatsApp] : [NotificationChannel.Sms], SystemHealthAlerts.RemindAfter);

    /// <summary>
    /// A server's alert for not reporting raised again: the alert of its last return, if it has not gone yet, is withdrawn,
    /// so that "reporting again" never reaches a phone after the new alert that it is not. Its other messages not sent by
    /// then are withdrawn with it: the new alert takes their place.
    /// </summary>
    private async Task WithdrawUnsentReturnAsync(AlertIncident raised, CancellationToken ct)
    {
        if (raised.Code != SystemHealthService.NotReportingCode)
        {
            return;
        }

        var last = await db.Set<AlertIncident>().AsNoTracking()
            .Where(i => i.Code == raised.Code && i.Server == raised.Server && i.ClearedAt != null)
            .OrderByDescending(i => i.ClearedAt)
            .FirstOrDefaultAsync(ct);
        if (last is not null)
        {
            await NotificationLogs.WithdrawAsync(db, AllMessageIds(last), WithdrawnBecauseSilentAgain, ct);
        }
    }

    private static List<Guid> AllMessageIds(AlertIncident incident) =>
        [.. incident.OnCallMessageIds, .. incident.SecondContactMessageIds, .. incident.SupportMessageIds];

    /// <summary>An escalation, by SMS with the same DLT-registered template, so it needs no registration of its own.</summary>
    private IReadOnlyList<Guid> Page(string phone, string name, string server, string message) =>
        notifications.Queue(AlertReadiness.AlertTemplate, new NotificationRecipient(Phone.Normalize(phone), null, null, name),
            new Dictionary<string, string> { ["server"] = server, ["message"] = message }, [NotificationChannel.Sms], SystemHealthAlerts.RemindAfter);

    private void Audit(AlertIncident incident, string phone, string to, DateTimeOffset now) =>
        audit.Add(new AuditEntry(AuditCategories.Admin, EscalatedAction,
            Clip($"Escalated the alert {incident.Code} to {to} ({Phone.Mask(Phone.Normalize(phone))}): not acknowledged {Minutes(incident, now)} min after it was told.", 1000))
        {
            EntityType = Entity,
            EntityId = incident.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });

    /// <summary>The incidents at whose level nothing has reached anyone (<see cref="Reaches"/>), or nothing was sent.</summary>
    private async Task<HashSet<Guid>> UndeliveredAsync(IReadOnlyList<AlertIncident> incidents, CancellationToken ct)
    {
        var waiting = incidents.Where(i => i.ToldAt is not null && i.AcknowledgedAt is null && i.ClearedAt is null).ToList();
        var ids = waiting.SelectMany(i => i.LevelMessageIds).ToList();
        var statuses = ids.Count == 0
            ? []
            : await db.Set<NotificationLog>().AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => new { l.Id, l.Status }).ToListAsync(ct);
        return
        [
            .. waiting.Where(i => !statuses.Any(s => i.LevelMessageIds.Contains(s.Id) && Reaches(s.Status))).Select(i => i.Id),
        ];
    }

    private async Task<List<AlertIncidentDto>> DtosAsync(IReadOnlyList<AlertIncident> incidents, CancellationToken ct)
    {
        if (incidents.Count == 0)
        {
            return [];
        }

        var ids = incidents.SelectMany(i => i.OnCallMessageIds.Concat(i.SecondContactMessageIds).Concat(i.SupportMessageIds)).ToList();
        var logs = ids.Count == 0 ? [] : await db.Set<NotificationLog>().AsNoTracking().Where(l => ids.Contains(l.Id)).ToListAsync(ct);

        // The in-app notifications are the alerts' own, matched by what they say and when they came.
        var told = incidents.Where(i => i.ToldAt is not null).ToList();
        var bodies = told.Select(i => Body(i.Message, i.Server)).Distinct().ToList();
        var since = told.Count == 0 ? DateTimeOffset.MaxValue : told.Min(i => i.ToldAt!.Value) - TimeSpan.FromMinutes(1);
        var inApp = told.Count == 0
            ? []
            : await db.Set<UserNotification>().AsNoTracking()
                .Where(n => n.Title == SystemHealthAlerts.Title && n.CreatedAt >= since && bodies.Contains(n.Body!))
                .Select(n => new { n.Body, n.CreatedAt, n.ReadAt })
                .ToListAsync(ct);

        return
        [
            .. incidents.Select(i =>
            {
                var body = Body(i.Message, i.Server);
                var read = i.ToldAt is { } at
                    ? inApp.Where(n => n.Body == body && n.CreatedAt >= at - TimeSpan.FromMinutes(1) && n.CreatedAt <= at + SystemHealthAlerts.Interval).Min(n => n.ReadAt)
                    : null;
                var messages = Messages(i, logs);
                return new AlertIncidentDto(
                    i.Id, i.Code, i.Server.Length == 0 ? null : i.Server, i.Message, i.Level, i.RaisedAt, i.ToldAt,
                    messages.Where(m => m.Level == AlertLevel.OnCall).Min(m => m.DeliveredAt), read, i.SecondContactAt, i.SupportAt,
                    i.AcknowledgedAt, i.AcknowledgedBy, i.ClearedAt,
                    i.AcknowledgedAt is { } acknowledged && i.ToldAt is { } toldAt ? Math.Round((acknowledged - toldAt).TotalMinutes, 1) : null,
                    messages);
            }),
        ];
    }

    private static List<AlertMessage> Messages(AlertIncident incident, IReadOnlyList<NotificationLog> logs) =>
    [
        .. new[] { (AlertLevel.OnCall, incident.OnCallMessageIds), (AlertLevel.SecondContact, incident.SecondContactMessageIds), (AlertLevel.Support, incident.SupportMessageIds) }
            .SelectMany(level => logs.Where(l => level.Item2.Contains(l.Id))
                .Select(l => new AlertMessage(level.Item1, l.Channel, l.Recipient, l.Status, l.CreatedAt, l.SentAt, l.DeliveredAt, l.Error)))
            .OrderBy(m => m.QueuedAt),
    ];

    /// <summary>The hospital's name, for HyFib support to know whose alert it is: its first facility.</summary>
    private async Task<string> HospitalAsync(CancellationToken ct) =>
        await db.Set<Facility>().AsNoTracking().Where(f => f.IsActive).OrderBy(f => f.CreatedAt).Select(f => f.Name).FirstOrDefaultAsync(ct)
        ?? "The hospital";

    /// <summary>
    /// The checks and acknowledgements take turns across the servers on the database, each in its transaction. Whoever also
    /// takes the drills' turn (<see cref="AlertDrills"/>) takes this one first, so the two never wait on each other.
    /// </summary>
    internal static Task TakeTurnAsync(HospitalDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({Entity}, 0))", ct);
    }

    /// <summary>What the in-app notification of a warning says.</summary>
    internal static string Body(string message, string? server) => string.IsNullOrEmpty(server) ? message : $"{message} (server {server})";

    private static int Minutes(AlertIncident incident, DateTimeOffset now) => (int)Math.Round((now - (incident.ToldAt ?? now)).TotalMinutes);

    /// <summary>A warning's key as its incident keeps it: code and server clipped to the columns' lengths, no server empty.</summary>
    private static string Key(HealthWarning warning) =>
        $"{Clip(warning.Code, AlertIncident.CodeLength)}|{Clip(warning.Server ?? string.Empty, AlertIncident.ServerLength)}";

    private static string Key(AlertIncident incident) => $"{incident.Code}|{incident.Server}";

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];
}
