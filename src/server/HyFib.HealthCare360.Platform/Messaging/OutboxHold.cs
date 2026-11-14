using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Messaging;

/// <summary>
/// Outgoing messages held after a restore: the hospital-wide setting <see cref="SettingKey"/>, which the restore sets as the
/// server starts. A restored database holds as waiting the messages that were waiting when the backup was made, and some of
/// them may have gone out since (a payment link, a bill, a result). So none of the messages queued before the hold began
/// goes to the internet until an administrator has checked them and released them (<see cref="OutboxHolds.ReleaseAsync"/>):
/// they stay waiting, and still expire when their time is up. Messages queued since are new and go as ever, the alert that
/// tells IT on-call of the restore among them, and so does the local lane (in-app alerts, the analysers' orders, Tally).
/// System health says how many are held.
/// </summary>
/// <param name="Reason">Why: <see cref="RestoreReason"/> when the restore set it, else words for the administrator.</param>
/// <param name="Since">When the hold began (UTC): messages queued at or before it are held. The default value when it is not known.</param>
/// <param name="BackupTakenAt">When the restored backup was made (UTC), when known: what was waiting then may have gone since.</param>
public sealed record OutboxHold(string Reason, DateTimeOffset Since, DateTimeOffset? BackupTakenAt)
{
    /// <summary>The setting, hospital-wide (no facility): an object in camelCase while messages are held, absent otherwise.</summary>
    public const string SettingKey = "messaging.outbox.hold";

    /// <summary>
    /// The reason the restore gives. System health then has a critical warning of its own for the restore
    /// (<c>system.restored</c>), which pages IT on-call, so the held messages' warning does not page them a second time.
    /// </summary>
    public const string RestoreReason = "restore";

    /// <summary>Whether the restore set the hold (<see cref="RestoreReason"/>).</summary>
    public bool SetByRestore() => string.Equals(Reason, RestoreReason, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What System health says while messages are held, <paramref name="held"/> of them, with the time in the hospital's
    /// zone. A hold that cannot be read holds every message on the internet lane.
    /// </summary>
    public string Warning(int held)
    {
        if (Since == default)
        {
            return "Outgoing messages are held, and the hold cannot be read: none goes to the internet. Check them, then release";
        }

        var queued = string.Create(CultureInfo.InvariantCulture, $"{TimeZoneInfo.ConvertTime(Since, HospitalTimeZone.Zone):d MMM yyyy HH:mm}");
        var before = SetByRestore() ? $"queued before the restore at {queued}" : $"queued before {queued}";
        var why = SetByRestore() || string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" ({Reason.Trim()})";
        return held switch
        {
            0 => $"No outgoing message {before} is held any more{why}: release the hold to end it",
            1 => $"1 outgoing message {before} is held{why}: check it, then release. Messages queued since go as usual",
            _ => string.Create(CultureInfo.InvariantCulture, $"{held} outgoing messages {before} are held{why}: check them, then release. Messages queued since go as usual"),
        };
    }
}

/// <summary>What releasing the hold did.</summary>
/// <param name="Held">The messages it held when it was released, which go now.</param>
public sealed record OutboxReleased(DateTimeOffset ReleasedAt, int Held);

/// <summary>Reads and releases the hold on outgoing messages (<see cref="OutboxHold"/>).</summary>
public static class OutboxHolds
{
    public const string ReleasedAction = "messaging.outbox_released";

    /// <summary>
    /// The hold, or null when outgoing messages are not held. A value that cannot be read holds them too, with
    /// <see cref="OutboxHold.Since"/> not known: something set it, and nothing should go until someone has looked.
    /// </summary>
    public static async Task<OutboxHold?> ReadAsync(ISettingsService settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            return await settings.GetAsync<OutboxHold>(OutboxHold.SettingKey, null, cancellationToken);
        }
        catch (JsonException)
        {
            return new OutboxHold($"The setting {OutboxHold.SettingKey} holds a value HC360 cannot read.", default, null);
        }
    }

    /// <summary>
    /// How many messages <paramref name="hold"/> holds: those waiting on the internet lane queued at or before it began, or
    /// all of them when it cannot be read. The local lane is never held, so what waits there is not counted.
    /// </summary>
    internal static Task<int> CountHeldAsync(HospitalDbContext db, OutboxHold hold, IEnumerable<IOutboxHandler> handlers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(hold);
        var local = OutboxProvider.LocalTypes(handlers).ToArray();
        var waiting = db.Set<OutboxMessage>().AsNoTracking().Where(m => m.Status == OutboxStatus.Pending && !local.Contains(m.Type));
        if (hold.Since != default)
        {
            var since = hold.Since;
            waiting = waiting.Where(m => m.CreatedAt <= since);
        }

        return waiting.CountAsync(ct);
    }

    /// <summary>
    /// Releases the hold: clears the setting, which ends the restore's warning on System health too, and audits what it
    /// said and how many messages it held. They go within seconds, on every server. 409 when messages are not held.
    /// </summary>
    internal static async Task<Result<OutboxReleased>> ReleaseAsync(
        HospitalDbContext db, ISettingsService settings, IAuditLogger audit, IEnumerable<IOutboxHandler> handlers, TimeProvider time, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var hold = await ReadAsync(settings, ct);
        var held = hold is null ? 0 : await CountHeldAsync(db, hold, handlers, ct);

        // The stored row decides: released by someone else meanwhile, it is gone, and this release is refused.
        if (!await settings.RemoveAsync(OutboxHold.SettingKey, null, ct))
        {
            return Error.Conflict("outbox.not_held", "Outgoing messages are not held: there is nothing to release.");
        }

        audit.Add(new AuditEntry(AuditCategories.Admin, ReleasedAction, Summary(hold, held))
        {
            EntityType = nameof(OutboxHold),
            EntityId = OutboxHold.SettingKey,
            Severity = AuditSeverity.Warning,
            Details = new ReleasedDetails(hold?.Reason, hold?.Since is { } since && since != default ? since : null, hold?.BackupTakenAt, held),
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new OutboxReleased(time.GetUtcNow(), held);
    }

    private static string Summary(OutboxHold? hold, int held) =>
        string.Create(CultureInfo.InvariantCulture, $"Released the hold on outgoing messages; it held {held}.")
        + (string.IsNullOrWhiteSpace(hold?.Reason) ? string.Empty : $" Its reason: {hold.Reason}");

    private sealed record ReleasedDetails(string? Reason, DateTimeOffset? Since, DateTimeOffset? BackupTakenAt, int Held);
}

/// <summary>Administration › System health: releasing the outgoing messages held after a restore.</summary>
internal static class OutboxHoldEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/v1/admin/system/outbox/release", async (
                    HospitalDbContext db, ISettingsService settings, IAuditLogger audit, [FromServices] IEnumerable<IOutboxHandler> handlers, TimeProvider time,
                    CancellationToken ct) =>
                (await OutboxHolds.ReleaseAsync(db, settings, audit, handlers, time, ct)).ToOk())
            .WithTags("Administration")
            .RequirePermission(PlatformPermissions.SystemManage);
}
