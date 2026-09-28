using System.Text.Json;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Realtime;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>Realtime push deferred until after commit (payload already serialised).</summary>
internal sealed record DeferredRealtime(string Topic, string Name, JsonElement Payload);

/// <summary>In-app staff alert deferred until after commit.</summary>
internal sealed record DeferredStaffAlert(string Permission, Guid FacilityId, string Title, string Body, string Link, string Severity);

/// <summary>
/// Pushes lab, radiology and cardiology changes to <c>Topics.Lab(facility)</c> so worklists refresh within seconds
/// (PERF-04). Payloads carry ids and status only; clients refetch the details with their own permissions. Changes
/// made inside another module's transaction (ServiceOrderPlaced handlers) are pushed through the outbox after commit,
/// so a screen never refetches work that was rolled back.
/// </summary>
internal sealed class DiagnosticsRealtime(IRealtimePublisher realtime, IOutbox outbox, ILogger<DiagnosticsRealtime> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RealtimeExpiry = TimeSpan.FromMinutes(5);

    /// <summary>Immediate push; call only after SaveChanges. A failed push never fails the request (screens refetch on reconnect).</summary>
    public Task PublishAsync(Guid facilityId, string eventName, object payload, CancellationToken ct) =>
        AfterCommit.RunAsync(logger, $"realtime {eventName}", () => realtime.PublishAsync(Topics.Lab(facilityId), eventName, payload, ct));

    public void PublishAfterCommit(Guid facilityId, string eventName, object payload) =>
        outbox.Enqueue(
            DiagnosticsOutboxTypes.Realtime,
            new DeferredRealtime(Topics.Lab(facilityId), eventName, JsonSerializer.SerializeToElement(payload, JsonOptions)),
            facilityId,
            expiresIn: RealtimeExpiry);

    /// <summary>In-app alert to permission holders, sent only if the surrounding transaction commits.</summary>
    public void AlertStaffAfterCommit(DeferredStaffAlert alert) =>
        outbox.Enqueue(DiagnosticsOutboxTypes.StaffAlert, alert, alert.FacilityId, expiresIn: TimeSpan.FromDays(1));
}

/// <summary>
/// Runs a side effect after the clinical change has been committed (in-app alert, realtime push). A failure is logged
/// and swallowed. Returning an error would make the client retry an action that already succeeded, for example
/// recording a QC run twice.
/// </summary>
internal static class AfterCommit
{
    public static async Task RunAsync(ILogger logger, string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Post-commit {Action} failed", what);
        }
    }
}

internal static class DiagnosticsOutboxTypes
{
    public const string Realtime = "diagnostics.realtime";
    public const string StaffAlert = "diagnostics.staff-alert";
}

/// <summary>Delivers realtime pushes deferred by <see cref="DiagnosticsRealtime.PublishAfterCommit"/>.</summary>
internal sealed class DiagnosticsRealtimeOutboxHandler(IRealtimePublisher realtime) : IOutboxHandler
{
    public string Type => DiagnosticsOutboxTypes.Realtime;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var push = message.Read<DeferredRealtime>();
        return realtime.PublishAsync(push.Topic, push.Name, push.Payload, cancellationToken);
    }
}

/// <summary>Delivers staff alerts deferred by <see cref="DiagnosticsRealtime.AlertStaffAfterCommit"/>.</summary>
internal sealed class DiagnosticsStaffAlertOutboxHandler(INotificationService notifications) : IOutboxHandler
{
    public string Type => DiagnosticsOutboxTypes.StaffAlert;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var alert = message.Read<DeferredStaffAlert>();
        return notifications.NotifyPermissionHoldersAsync(
            alert.Permission, alert.FacilityId, alert.Title, alert.Body, alert.Link, "diagnostics", alert.Severity, cancellationToken);
    }
}
