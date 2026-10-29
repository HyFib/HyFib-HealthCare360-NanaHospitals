using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>A message a drill's warning went out in, as the message log has it now.</summary>
/// <param name="Recipient">The number it went to, masked as the message log keeps it.</param>
/// <param name="QueuedAt">When the alerts queued it.</param>
/// <param name="SentAt">When the provider took it.</param>
/// <param name="DeliveredAt">When the provider's delivery receipt said it reached the phone.</param>
public sealed record AlertDrillMessage(
    NotificationChannel Channel, string? Recipient, NotificationStatus Status, DateTimeOffset QueuedAt, DateTimeOffset? SentAt,
    DateTimeOffset? DeliveredAt, string? Error);

/// <summary>An alert drill, and when its warning got how far.</summary>
/// <param name="State"><c>open</c> until someone acknowledges it, then <c>acknowledged</c>; <c>expired</c> after an hour without.</param>
/// <param name="ToldAt">When the alerts told it, in the app and to IT on-call; null while they have not.</param>
/// <param name="ToldTo">How many of those who may see System health had it in the app.</param>
/// <param name="FirstReadAt">When the first of them opened it in the app.</param>
public sealed record AlertDrill(
    Guid Id, string Code, DateTimeOffset StartedAt, string? StartedBy, string State, DateTimeOffset? ToldAt, int ToldTo,
    IReadOnlyList<AlertDrillMessage> Messages, DateTimeOffset? FirstReadAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy);

/// <summary>
/// Alert drills (audit P2): whether a critical warning reaches IT, and how fast, found out before a real one. A drill raises
/// a test critical warning in System health for an hour, or until someone acknowledges it. The alerts tell it as they tell
/// any (<see cref="SystemHealthAlerts"/>): within five minutes, in the app to everyone who may see System health, and to the
/// IT on-call number by SMS and, where the hospital chose, WhatsApp. The drill keeps when each step happened, from the audit
/// trail, the message log and the in-app notifications, so a drill at go-live and every quarter shows that the chain works
/// and how long it takes, or where it breaks.
/// </summary>
public sealed class AlertDrills(HospitalDbContext db, IAuditLogger audit, ICurrentUser user, TimeProvider time)
{
    public const string StartedAction = "alerts.drill_started";
    public const string ToldAction = "alerts.drill_told";
    public const string AcknowledgedAction = "alerts.drill_acknowledged";

    /// <summary>The drill's warning code, followed by the drill's code: each drill is told afresh, however soon after another.</summary>
    public const string WarningPrefix = "alerts.drill:";

    /// <summary>How long a drill waits to be acknowledged.</summary>
    public static readonly TimeSpan OpenFor = TimeSpan.FromHours(1);

    private const string Entity = "AlertDrill";
    private const int Listed = 10;

    /// <summary>How far back the drills listed go.</summary>
    private static readonly TimeSpan Kept = TimeSpan.FromDays(366);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Starts a drill, unless one is open.</summary>
    public async Task<Result<AlertDrill>> StartAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await TakeTurnAsync(ct);
        var now = time.GetUtcNow();
        if (await OpenAsync(db, now, ct) is { } open)
        {
            return Error.Conflict("alerts.drill_open", $"Drill {open.Code} is still open: acknowledge it before starting another.");
        }

        var id = Guid.CreateVersion7(now);
        var code = CodeOf(id);
        audit.Add(new AuditEntry(AuditCategories.Admin, StartedAction, $"Started alert drill {code}.")
        {
            EntityType = Entity,
            EntityId = id.ToString(),
            Details = new StartedDetails(code, user.DisplayName),
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (await BuildAsync(await StartsAsync(now - OpenFor, id, ct), ct)).Single();
    }

    /// <summary>Acknowledges an open drill: its warning reached whoever acknowledges it, and System health stops showing it.</summary>
    public async Task<Result<AlertDrill>> AcknowledgeAsync(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await TakeTurnAsync(ct);
        var start = await StartsAsync(time.GetUtcNow() - Kept, id, ct);
        if (start.Count == 0)
        {
            return Error.NotFound("Alert drill", id);
        }

        var drill = (await BuildAsync(start, ct)).Single();
        if (drill.State != "open")
        {
            return Error.Conflict("alerts.drill_closed", drill.State == "acknowledged"
                ? string.Create(CultureInfo.InvariantCulture, $"Drill {drill.Code} was acknowledged by {drill.AcknowledgedBy ?? "someone"} at {drill.AcknowledgedAt:HH:mm} UTC.")
                : $"Drill {drill.Code} expired: nobody acknowledged it within an hour.");
        }

        audit.Add(new AuditEntry(AuditCategories.Admin, AcknowledgedAction, $"Acknowledged alert drill {drill.Code}.")
        {
            EntityType = Entity,
            EntityId = id.ToString(),
            Details = new AcknowledgedDetails(user.DisplayName),
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (await BuildAsync(start, ct)).Single();
    }

    /// <summary>The drills of the last year, the latest first.</summary>
    public async Task<IReadOnlyList<AlertDrill>> ListAsync(CancellationToken ct)
    {
        var started = await Events(db, StartedAction, time.GetUtcNow() - Kept)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(Listed)
            .ToListAsync(ct);
        return await BuildAsync(started, ct);
    }

    /// <summary>System health's critical warning while a drill is open.</summary>
    public static async Task<HealthWarning?> WarningAsync(HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(time);
        return await OpenAsync(db, time.GetUtcNow(), ct) is { } open
            ? new HealthWarning(WarningPrefix + open.Code, "critical", Message(open.Code, open.StartedBy))
            : null;
    }

    /// <summary>What a drill's warning says, in System health, in the app and in the SMS to IT on-call.</summary>
    public static string Message(string code, string? startedBy) =>
        $"Alert drill {code}{(startedBy is null ? string.Empty : $", started by {startedBy}")}: a test that critical warnings reach IT. "
        + "Nothing is wrong. Acknowledge it under Administration › System health.";

    /// <summary>
    /// Records that the alerts told the drill whose warning is <paramref name="warningCode"/>, with the ids of the message log
    /// entries of what they queued to IT on-call.
    /// </summary>
    internal static async Task RecordToldAsync(
        HospitalDbContext db, IAuditLogger audit, TimeProvider time, string warningCode, IReadOnlyList<Guid> logIds, CancellationToken ct)
    {
        var code = warningCode[WarningPrefix.Length..];
        var starts = await Events(db, StartedAction, time.GetUtcNow() - OpenFor - SystemHealthAlerts.Interval).ToListAsync(ct);
        if (starts.FirstOrDefault(e => Read<StartedDetails>(e.Details)?.Code == code) is not { } start)
        {
            return;
        }

        await audit.WriteAsync(new AuditEntry(AuditCategories.Admin, ToldAction,
            string.Create(CultureInfo.InvariantCulture, $"Told alert drill {code}: in the app, and in {logIds.Count} message(s) to IT on-call."))
        {
            EntityType = Entity,
            EntityId = start.EntityId,
            Details = new ToldDetails(logIds),
        }, ct);
    }

    /// <summary>
    /// Starts and acknowledgements take turns, across the servers on the database, each writing in the transaction that
    /// checked: two starts at once (a double click, or two people) cannot both find no drill open.
    /// </summary>
    private Task TakeTurnAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({Entity}, 0))", ct);

    private static async Task<OpenDrill?> OpenAsync(HospitalDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var start = await Events(db, StartedAction, now - OpenFor).OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).FirstOrDefaultAsync(ct);
        if (start is null || await Events(db, AcknowledgedAction, start.OccurredAt).AnyAsync(e => e.EntityId == start.EntityId, ct))
        {
            return null;
        }

        var details = Read<StartedDetails>(start.Details);
        return new OpenDrill(details?.Code ?? CodeOf(Guid.Parse(start.EntityId!, CultureInfo.InvariantCulture)), details?.StartedBy);
    }

    private Task<List<AuditEvent>> StartsAsync(DateTimeOffset since, Guid id, CancellationToken ct)
    {
        var entityId = id.ToString();
        return Events(db, StartedAction, since).Where(e => e.EntityId == entityId).ToListAsync(ct);
    }

    /// <summary>The drills started at <paramref name="started"/>, each with what followed it.</summary>
    private async Task<IReadOnlyList<AlertDrill>> BuildAsync(IReadOnlyList<AuditEvent> started, CancellationToken ct)
    {
        if (started.Count == 0)
        {
            return [];
        }

        var ids = started.Select(e => e.EntityId).ToList();
        var since = started.Min(e => e.OccurredAt);
        var followed = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Category == AuditCategories.Admin && (e.Action == ToldAction || e.Action == AcknowledgedAction)
                && e.OccurredAt >= since && ids.Contains(e.EntityId))
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(ct);

        var logIds = followed.Where(e => e.Action == ToldAction).SelectMany(e => Read<ToldDetails>(e.Details)?.LogIds ?? []).ToList();
        var logs = logIds.Count == 0
            ? []
            : await db.Set<NotificationLog>().AsNoTracking().Where(l => logIds.Contains(l.Id)).ToListAsync(ct);

        // The in-app notifications are the alerts' own, matched by what they say; one query for every drill listed.
        var starts = started.Select(e => (Event: e, Details: Read<StartedDetails>(e.Details))).ToList();
        var messages = starts.Select(s => Message(s.Details?.Code ?? string.Empty, s.Details?.StartedBy)).ToList();
        var inApp = await db.Set<UserNotification>().AsNoTracking()
            .Where(n => n.Title == SystemHealthAlerts.Title && n.CreatedAt >= since && messages.Contains(n.Body!))
            .Select(n => new { n.Body, n.CreatedAt, n.ReadAt })
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        return
        [
            .. starts.Select(s =>
            {
                var told = followed.Where(e => e.EntityId == s.Event.EntityId && e.Action == ToldAction).ToList();
                var acknowledged = followed.FirstOrDefault(e => e.EntityId == s.Event.EntityId && e.Action == AcknowledgedAction);
                var code = s.Details?.Code ?? string.Empty;
                var message = Message(code, s.Details?.StartedBy);
                var notified = inApp.Where(n => n.Body == message && n.CreatedAt >= s.Event.OccurredAt).ToList();
                var sent = told.SelectMany(e => Read<ToldDetails>(e.Details)?.LogIds ?? []).ToHashSet();
                return new AlertDrill(
                    Guid.Parse(s.Event.EntityId!, CultureInfo.InvariantCulture),
                    code,
                    s.Event.OccurredAt,
                    s.Details?.StartedBy,
                    acknowledged is not null ? "acknowledged" : now - s.Event.OccurredAt >= OpenFor ? "expired" : "open",
                    told.Count == 0 ? null : told[0].OccurredAt,
                    notified.Count,
                    [
                        .. logs.Where(l => sent.Contains(l.Id)).OrderBy(l => l.CreatedAt)
                            .Select(l => new AlertDrillMessage(l.Channel, l.Recipient, l.Status, l.CreatedAt, l.SentAt, l.DeliveredAt, l.Error)),
                    ],
                    notified.Min(n => n.ReadAt),
                    acknowledged?.OccurredAt,
                    acknowledged is null ? null : Read<AcknowledgedDetails>(acknowledged.Details)?.By ?? acknowledged.UserName);
            }),
        ];
    }

    private static IQueryable<AuditEvent> Events(HospitalDbContext db, string action, DateTimeOffset since)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Category == AuditCategories.Admin && e.Action == action && e.OccurredAt >= since);
    }

    /// <summary>The drill's code, to say on the phone and read in the SMS: the last six hex digits of its random part.</summary>
    private static string CodeOf(Guid id) => id.ToString("N")[^6..].ToUpperInvariant();

    private static T? Read<T>(string? details) => details is null ? default : JsonSerializer.Deserialize<T>(details, Json);

    private sealed record OpenDrill(string Code, string? StartedBy);

    private sealed record StartedDetails(string Code, string? StartedBy);

    private sealed record ToldDetails(IReadOnlyList<Guid> LogIds);

    private sealed record AcknowledgedDetails(string? By);
}
