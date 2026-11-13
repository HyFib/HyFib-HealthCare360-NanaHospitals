using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
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
/// <item>An analyser result flagged critical that is kept among the unmatched results is on no test, so no pathologist
/// hears of it: whoever assigns unmatched results is told to match it now.</item>
/// </list>
/// The alerts after commit name the patient when the look-up works; when it fails they go without the name.
/// </summary>
internal sealed class CriticalValueNotifier(
    DiagnosticsLookups lookups, INotificationService notifications, DiagnosticsRealtime realtime, ILogger<CriticalValueNotifier> logger)
{
    public const string LabCriticalTemplate = "lab.critical";
    public const string FindingCriticalTemplate = "diagnostics.critical_finding";

    /// <summary>A critical SMS older than a shift is no longer actionable; the call register and in-app alerts remain.</summary>
    private static readonly TimeSpan SmsExpiry = TimeSpan.FromHours(12);

    /// <summary>
    /// After commit: tell the pathologists a critical value is waiting for verification. Nothing here may fail the caller:
    /// the value is saved, and an analyser whose message failed would send it again, find it stored and alert nobody.
    /// </summary>
    public async Task AlertVerifiersAsync(IReadOnlyCollection<CriticalAlert> alerts, CancellationToken ct)
    {
        foreach (var alert in alerts)
        {
            var patient = await PatientOrNullAsync(alert.PatientId, ct);
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
            var (title, body) = Words(alert, await PatientOrNullAsync(alert.PatientId, ct));
            if (alert.OrderingPractitionerId is { } practitionerId)
            {
                await SafeAsync(async () =>
                {
                    var users = await lookups.UserIdsForPractitionerAsync(practitionerId, ct);
                    await notifications.NotifyUsersAsync(users, title, body, alert.Link, "diagnostics", "critical", ct);
                });
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

    /// <summary>
    /// Before SaveChanges: an analyser result kept among the unmatched results (no sample id, an unknown or cancelled tube,
    /// a value its test cannot hold…) is on no test, so no pathologist is alerted to it. One the analyser flagged critical
    /// (<see cref="AnalyserFlags"/>) is put to whoever can assign it, through the outbox in the transaction that keeps it:
    /// told only if it is kept, and once, as a message sent again keeps nothing more.
    /// </summary>
    public void AlertUnmatchedAfterCommit(LabInstrument instrument, IEnumerable<UnmatchedLabResult> kept)
    {
        foreach (var alert in kept.Select(row => UnmatchedAlert(instrument, row)).OfType<DeferredStaffAlert>())
        {
            realtime.AlertStaffAfterCommit(alert);
        }
    }

    /// <summary>
    /// The alert for an analyser result kept aside that the analyser flagged critical, to whoever assigns unmatched results
    /// (<see cref="DiagnosticsPermissions.LabResultsEnter"/>), with the analyser, the sample id and the test; null for any
    /// other, and for the analyser's withdrawal of a result, which is no value.
    /// </summary>
    internal static DeferredStaffAlert? UnmatchedAlert(LabInstrument instrument, UnmatchedLabResult kept)
    {
        if (kept.Reason == UnmatchedResultReason.Withdrawn || !AnalyserFlags.IsCritical(kept.InstrumentFlags))
        {
            return null;
        }

        var value = kept.InstrumentUnit is { } unit ? $"{kept.Value} {unit}" : kept.Value;
        var sample = kept.SampleBarcode is { } barcode ? $"for sample {barcode}" : "without a sample id";
        return new DeferredStaffAlert(
            DiagnosticsPermissions.LabResultsEnter,
            kept.FacilityId,
            $"Critical analyser result to match: {kept.ChannelCode} from {instrument.Code}",
            $"{instrument.Name} ({instrument.Code}) sent {kept.ChannelCode} {value}, flagged {kept.InstrumentFlags}, {sample}. It went on no test, "
            + $"so no pathologist has been alerted: {kept.Detail} Match it to the right sample's test now.",
            $"/lab/unmatched?search={Uri.EscapeDataString(kept.SampleBarcode ?? kept.ChannelCode)}",
            "critical");
    }

    /// <summary>
    /// The patient an alert after commit names; null, logged, when the look-up fails, and the alert goes without the name
    /// rather than not at all.
    /// </summary>
    private async Task<PatientSnapshot?> PatientOrNullAsync(Guid patientId, CancellationToken ct)
    {
        try
        {
            return await lookups.PatientAsync(patientId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not look up the patient of a critical-value alert; it is sent without the name");
            return null;
        }
    }

    private static (string Title, string Body) Words(CriticalAlert alert, PatientSnapshot? patient) =>
        (alert.Kind == DiagnosticKind.Laboratory ? $"Critical value: {alert.What}" : $"Critical finding: {alert.What}",
            $"{Describe(patient)}: {alert.What} {alert.Value}");

    private static string Describe(PatientSnapshot? patient) => patient is null ? "Patient" : $"{patient.FullName} ({patient.Uhid})";

    /// <summary>The clinical change is committed and the call is documented; a failed extra alert is logged, not rethrown.</summary>
    private Task SafeAsync(Func<Task> send) => AfterCommit.RunAsync(logger, "critical-value alert", send);
}
