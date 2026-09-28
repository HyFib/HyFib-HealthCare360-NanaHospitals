using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// Raises and routes RPM alerts (RPM-03/05). New or escalated alerts page the care team by SMS/WhatsApp (queued in the
/// same transaction as the readings) and every holder of <c>rpm.alerts.manage</c> in the facility in-app (after commit,
/// so staff are never alerted about data that was rolled back). A silent device also nudges the patient.
/// </summary>
public sealed class RpmAlertService(HospitalDbContext db, INotificationService notifications, ILogger<RpmAlertService> logger)
{
    public const string CareTeamTemplate = "rpm.alert";
    public const string DeviceSilentTemplate = "rpm.device_silent";

    private static readonly NotificationChannel[] Channels = [NotificationChannel.Sms, NotificationChannel.WhatsApp];

    /// <summary>Opens or escalates alerts for out-of-range readings; returns the alerts that need notifying.</summary>
    public async Task<IReadOnlyList<RpmAlert>> RaiseForReadingsAsync(RpmEnrollment enrollment, IReadOnlyList<RpmReading> outOfRange, DateTimeOffset now, CancellationToken ct)
    {
        if (outOfRange.Count == 0)
        {
            return [];
        }

        var types = outOfRange.Select(r => r.Type).Distinct().ToList();
        var active = await db.Set<RpmAlert>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(a => a.EnrollmentId == enrollment.Id && a.Kind == RpmAlertKind.Threshold
                        && (a.Status == RpmAlertStatus.Open || a.Status == RpmAlertStatus.Acknowledged)
                        && a.ReadingType != null && types.Contains(a.ReadingType.Value))
            .ToListAsync(ct);

        var toNotify = new List<RpmAlert>();
        foreach (var reading in outOfRange.OrderBy(r => r.MeasuredAt))
        {
            var existing = active.FirstOrDefault(a => a.ReadingType == reading.Type);
            if (existing is null)
            {
                var alert = RpmAlert.ForReading(enrollment, reading, now);
                db.Add(alert);
                active.Add(alert);
                toNotify.Add(alert);
            }
            else if (existing.Recur(reading, now) && !toNotify.Contains(existing))
            {
                toNotify.Add(existing);
            }
        }

        await QueueCareTeamAsync(enrollment, toNotify, ct);
        return toNotify;
    }

    /// <summary>RPM-05: records a silence alert for the device and reminds the patient to take a reading.</summary>
    public async Task<RpmAlert> RaiseSilenceAsync(RpmEnrollment enrollment, RpmDevice device, TimeSpan silentFor, DateTimeOffset now, CancellationToken ct)
    {
        var alert = RpmAlert.ForSilence(enrollment, device, silentFor, now);
        db.Add(alert);
        device.MarkSilenceAlerted(now);

        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == enrollment.PatientId)
            .Select(p => new { p.Id, p.FullName, p.Phone, p.Email })
            .FirstOrDefaultAsync(ct);
        if (patient?.Phone is not null)
        {
            var since = HospitalCalendar.India.ToLocal(device.LastSeenAt ?? device.AssignedAt ?? now - silentFor);
            notifications.Queue(
                DeviceSilentTemplate,
                new NotificationRecipient(patient.Phone, patient.Email, patient.Id, patient.FullName),
                new Dictionary<string, string>
                {
                    ["name"] = patient.FullName,
                    ["device"] = DeviceName(device.Type),
                    ["since"] = since.ToString("dd MMM, hh:mm tt", CultureInfo.InvariantCulture),
                },
                Channels,
                TimeSpan.FromHours(12),
                enrollment.FacilityId);
        }

        return alert;
    }

    /// <summary>In-app notifications to every alert handler of the facility (call after the alerts are committed).</summary>
    public async Task NotifyInAppAsync(IReadOnlyCollection<RpmAlert> alerts, CancellationToken ct)
    {
        if (alerts.Count == 0)
        {
            return;
        }

        var patientIds = alerts.Select(a => a.PatientId).Distinct().ToList();
        var patients = await db.Set<Patient>().AsNoTracking()
            .Where(p => patientIds.Contains(p.Id))
            .Select(p => new { p.Id, p.FirstName, p.Uhid })
            .ToDictionaryAsync(p => p.Id, p => $"{p.FirstName} ({p.Uhid})", ct);

        foreach (var alert in alerts)
        {
            try
            {
                var critical = alert.Severity == RpmAlertSeverity.Critical;
                await notifications.NotifyPermissionHoldersAsync(
                    VirtualCarePermissions.RpmAlerts,
                    alert.FacilityId,
                    $"{(critical ? "Critical" : "Remote monitoring")} alert: {patients.GetValueOrDefault(alert.PatientId, "patient")}",
                    alert.Message,
                    $"/virtual-care/rpm/enrollments/{alert.EnrollmentId}?alert={alert.Id}",
                    "rpm",
                    critical ? "critical" : "warning",
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The alert itself is stored and listed; a failed bell notification must not fail ingestion.
                logger.LogWarning(ex, "In-app notification for RPM alert {AlertId} failed", alert.Id);
            }
        }
    }

    private async Task QueueCareTeamAsync(RpmEnrollment enrollment, IReadOnlyList<RpmAlert> alerts, CancellationToken ct)
    {
        if (alerts.Count == 0 || enrollment.CareTeamPractitionerIds.Count == 0)
        {
            return;
        }

        var ids = enrollment.CareTeamPractitionerIds;
        var team = await db.Set<Practitioner>().AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.IsActive && p.Phone != null)
            .Select(p => new { p.DisplayName, p.Phone, p.Email })
            .ToListAsync(ct);
        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == enrollment.PatientId)
            .Select(p => new { p.FirstName, p.Uhid })
            .FirstOrDefaultAsync(ct);

        // Minimum necessary PHI in SMS: first name and UHID only.
        var label = patient is null ? "A monitored patient" : $"{patient.FirstName} ({patient.Uhid})";
        foreach (var alert in alerts)
        {
            var when = HospitalCalendar.India.ToLocal(alert.LastOccurredAt).ToString("dd MMM, hh:mm tt", CultureInfo.InvariantCulture);
            foreach (var member in team)
            {
                notifications.Queue(
                    CareTeamTemplate,
                    new NotificationRecipient(member.Phone, member.Email, null, member.DisplayName),
                    new Dictionary<string, string> { ["patient"] = label, ["reading"] = alert.Message, ["time"] = when },
                    Channels,
                    TimeSpan.FromHours(6),
                    enrollment.FacilityId);
            }
        }
    }

    private static string DeviceName(RpmDeviceType type) => type switch
    {
        RpmDeviceType.BpMonitor => "BP monitor",
        RpmDeviceType.Glucometer => "glucometer",
        RpmDeviceType.PulseOximeter => "pulse oximeter",
        RpmDeviceType.WeightScale => "weighing scale",
        RpmDeviceType.Thermometer => "thermometer",
        _ => "ECG patch",
    };
}
