using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>One critical value (lab) or critical finding (radiology/cardiology) to announce.</summary>
internal sealed record CriticalAlert(
    DiagnosticKind Kind, Guid FacilityId, Guid PatientId, Guid? OrderingPractitionerId, string What, string Value, string Link);

/// <summary>
/// Electronic alerting for critical values and findings (LIS-05, IMG-03). The documented phone call
/// (CriticalCallLog) is the primary, mandatory channel. These alerts add redundancy so that a critical result cannot
/// go unnoticed:
/// <list type="number">
/// <item>At entry or analyser time, pathologists get an in-app alert to verify the value and phone it in quickly.</item>
/// <item>At verification, the ordering doctor gets an SMS (template <c>lab.critical</c> or <c>diagnostics.critical_finding</c>),
/// queued in the same transaction so the outbox delivers it through internet outages, plus an in-app alert. Holders of
/// <c>diagnostics.critical.alerts</c> are alerted as well.</item>
/// </list>
/// </summary>
internal sealed class CriticalValueNotifier(
    DiagnosticsLookups lookups, INotificationService notifications, DiagnosticsRealtime realtime, ILogger<CriticalValueNotifier> logger)
{
    public const string LabCriticalTemplate = "lab.critical";
    public const string FindingCriticalTemplate = "diagnostics.critical_finding";

    /// <summary>A critical SMS older than a shift is no longer actionable; the call register and in-app alerts remain.</summary>
    private static readonly TimeSpan SmsExpiry = TimeSpan.FromHours(12);

    /// <summary>After commit: tell the pathologists a critical value is waiting for verification.</summary>
    public async Task AlertVerifiersAsync(IReadOnlyCollection<CriticalAlert> alerts, CancellationToken ct)
    {
        foreach (var alert in alerts)
        {
            var patient = await lookups.PatientAsync(alert.PatientId, ct);
            await SafeAsync(() => notifications.NotifyPermissionHoldersAsync(
                DiagnosticsPermissions.LabResultsVerify,
                alert.FacilityId,
                $"Critical value awaiting verification: {alert.What}",
                $"{Describe(patient)}: {alert.What} {alert.Value}",
                alert.Link,
                "diagnostics",
                "critical",
                ct));
            await realtime.PublishAsync(alert.FacilityId, "lab.critical", new { alert.PatientId, alert.Kind }, ct);
        }
    }

    /// <summary>Before SaveChanges: queues the SMS to the ordering doctor in the caller's transaction.</summary>
    public async Task QueueDoctorSmsAsync(IReadOnlyCollection<CriticalAlert> alerts, CancellationToken ct)
    {
        foreach (var alert in alerts)
        {
            var doctor = await lookups.PractitionerAsync(alert.OrderingPractitionerId, ct);
            if (string.IsNullOrWhiteSpace(doctor?.Phone))
            {
                continue;
            }

            var patient = await lookups.PatientAsync(alert.PatientId, ct);
            var name = patient?.FullName ?? "Patient";
            var uhid = patient?.Uhid ?? "-";
            var values = alert.Kind == DiagnosticKind.Laboratory
                ? new Dictionary<string, string> { ["patient"] = name, ["uhid"] = uhid, ["test"] = alert.What, ["value"] = alert.Value }
                : new Dictionary<string, string> { ["patient"] = name, ["uhid"] = uhid, ["study"] = alert.What, ["finding"] = alert.Value };

            notifications.Queue(
                alert.Kind == DiagnosticKind.Laboratory ? LabCriticalTemplate : FindingCriticalTemplate,
                new NotificationRecipient(doctor.Phone, doctor.Email, alert.PatientId, doctor.DisplayName),
                values,
                [NotificationChannel.Sms],
                SmsExpiry,
                alert.FacilityId);
        }
    }

    /// <summary>After commit: in-app alerts to the ordering doctor's account(s) and to critical-alert permission holders.</summary>
    public async Task NotifyVerifiedAsync(IReadOnlyCollection<CriticalAlert> alerts, CancellationToken ct)
    {
        foreach (var alert in alerts)
        {
            var (title, body) = Words(alert, await lookups.PatientAsync(alert.PatientId, ct));
            if (alert.OrderingPractitionerId is { } practitionerId)
            {
                var users = await lookups.UserIdsForPractitionerAsync(practitionerId, ct);
                await SafeAsync(() => notifications.NotifyUsersAsync(users, title, body, alert.Link, "diagnostics", "critical", ct));
            }

            await SafeAsync(() => notifications.NotifyPermissionHoldersAsync(
                DiagnosticsPermissions.CriticalAlerts, alert.FacilityId, title, body, alert.Link, "diagnostics", "critical", ct));
        }
    }

    /// <summary>
    /// Before SaveChanges: the same in-app alerts as <see cref="NotifyVerifiedAsync"/>, sent only if the caller's
    /// transaction commits (a correction released by an approval, in the approval's transaction).
    /// </summary>
    public async Task NotifyVerifiedAfterCommitAsync(IReadOnlyCollection<CriticalAlert> alerts, CancellationToken ct)
    {
        foreach (var alert in alerts)
        {
            var (title, body) = Words(alert, await lookups.PatientAsync(alert.PatientId, ct));
            if (alert.OrderingPractitionerId is { } practitionerId)
            {
                var users = await lookups.UserIdsForPractitionerAsync(practitionerId, ct);
                realtime.AlertUsersAfterCommit(new DeferredUserAlert([.. users], alert.FacilityId, title, body, alert.Link, "critical"));
            }

            realtime.AlertStaffAfterCommit(new DeferredStaffAlert(DiagnosticsPermissions.CriticalAlerts, alert.FacilityId, title, body, alert.Link, "critical"));
        }
    }

    private static (string Title, string Body) Words(CriticalAlert alert, PatientSnapshot? patient) =>
        (alert.Kind == DiagnosticKind.Laboratory ? $"Critical value: {alert.What}" : $"Critical finding: {alert.What}",
            $"{Describe(patient)}: {alert.What} {alert.Value}");

    private static string Describe(PatientSnapshot? patient) => patient is null ? "Patient" : $"{patient.FullName} ({patient.Uhid})";

    /// <summary>The clinical change is committed and the call is documented; a failed extra alert is logged, not rethrown.</summary>
    private Task SafeAsync(Func<Task> send) => AfterCommit.RunAsync(logger, "critical-value alert", send);
}
