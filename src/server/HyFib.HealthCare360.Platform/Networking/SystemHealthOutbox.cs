using System.Globalization;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// The messages waiting to send, on System health (DED-17): how many wait, how many ended unsent in the last day (failed
/// after their tries, or expired before any could be made), and, while they are held after a restore, how many are held.
/// </summary>
internal static class SystemHealthOutbox
{
    /// <summary>The warning while outgoing messages are held (<see cref="OutboxHold"/>).</summary>
    public const string HeldCode = "outbox.held";

    /// <summary>How long the oldest waiting message may wait before System health says so.</summary>
    public static readonly TimeSpan BacklogAfter = TimeSpan.FromMinutes(30);

    public static async Task<OutboxInfo> ReadAsync(
        HospitalDbContext db, ISettingsService settings, IEnumerable<IOutboxHandler> handlers, DateTimeOffset now, CancellationToken ct)
    {
        var dayAgo = now.AddDays(-1);
        var messages = db.Set<OutboxMessage>().AsNoTracking();
        var hold = await OutboxHolds.ReadAsync(settings, ct);
        return new OutboxInfo(
            await messages.CountAsync(m => m.Status == OutboxStatus.Pending, ct),
            // When it ended, not when it was queued: a message is tried for up to its lifetime (days) before it fails.
            await messages.CountAsync(m => m.Status == OutboxStatus.Failed && (m.ProcessedAt ?? m.CreatedAt) > dayAgo, ct),
            await messages.Where(m => m.Status == OutboxStatus.Pending).OrderBy(m => m.CreatedAt).Select(m => (DateTimeOffset?)m.CreatedAt).FirstOrDefaultAsync(ct),
            // Never tried before its time was up: held after a restore, its provider down all along, or the server off.
            await messages.CountAsync(m => m.Status == OutboxStatus.Expired && (m.ProcessedAt ?? m.CreatedAt) > dayAgo, ct),
            hold,
            hold is null ? 0 : await OutboxHolds.CountHeldAsync(db, hold, handlers, ct));
    }

    /// <summary>
    /// The warnings about the messages: held, with how many (see <see cref="Held"/>), ended unsent in the last day, and
    /// waiting longer than <see cref="BacklogAfter"/>.
    /// </summary>
    public static IReadOnlyList<HealthWarning> Warnings(OutboxInfo outbox, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        var warnings = new List<HealthWarning>();
        if (Held(outbox) is { } held)
        {
            warnings.Add(held);
        }

        if (Unsent(outbox) is { } unsent)
        {
            warnings.Add(new("outbox.failed", "warning", unsent));
        }

        if (outbox.OldestPendingAt is { } oldest && now - oldest > BacklogAfter)
        {
            warnings.Add(new("outbox.backlog", "warning", outbox.Hold is null
                ? "Outbound messages are queued for more than 30 minutes (internet down?)."
                : "Outbound messages are queued for more than 30 minutes: those queued before the hold began are held."));
        }

        return warnings;
    }

    /// <summary>
    /// The held messages' warning. After a restore it is a warning only: the restore's own critical warning
    /// (<c>system.restored</c>) tells IT on-call, once. A hold set for any other reason is critical, so the alerts tell it.
    /// </summary>
    private static HealthWarning? Held(OutboxInfo outbox) =>
        outbox.Hold is not { } hold
            ? null
            : new(HeldCode, hold.SetByRestore() && hold.Since != default ? "warning" : "critical", hold.Warning(outbox.HeldMessages));

    private static string? Unsent(OutboxInfo outbox) => (outbox.FailedLast24Hours, outbox.ExpiredLast24Hours) switch
    {
        (0, 0) => null,
        (var failed, 0) => string.Create(CultureInfo.InvariantCulture, $"{failed} outbound message(s) failed in the last 24 hours."),
        (0, var expired) => string.Create(CultureInfo.InvariantCulture,
            $"{expired} outbound message(s) expired unsent in the last 24 hours: their time was up before they could be tried."),
        var (failed, expired) => string.Create(CultureInfo.InvariantCulture,
            $"{failed} outbound message(s) failed and {expired} expired unsent in the last 24 hours."),
    };
}
