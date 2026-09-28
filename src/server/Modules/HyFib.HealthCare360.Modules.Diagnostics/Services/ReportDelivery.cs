using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Tells the patient that a report is ready (LIS-07): template <c>lab.report_ready</c> over SMS and WhatsApp, plus e-mail
/// when the patient has an address. The message is queued in the release transaction and sent through the outbox, so
/// it goes out even if the internet is down at the moment of release. The report itself is opened on the patient
/// portal and never sent as message text.
/// </summary>
internal sealed class ReportDelivery(DiagnosticsLookups lookups, INotificationService notifications)
{
    public const string ReportReadyTemplate = "lab.report_ready";

    public async Task QueueReportReadyAsync(Guid patientId, string reference, CancellationToken ct)
    {
        var patient = await lookups.PatientAsync(patientId, ct);

        // Deceased patients' contacts are not messaged; the family collects reports at the counter.
        if (patient is null || patient.IsDeceased || (patient.Phone is null && patient.Email is null))
        {
            return;
        }

        var channels = new List<NotificationChannel>();
        if (patient.Phone is not null)
        {
            channels.Add(NotificationChannel.Sms);
            channels.Add(NotificationChannel.WhatsApp);
        }

        if (patient.Email is not null)
        {
            channels.Add(NotificationChannel.Email);
        }

        notifications.Queue(
            ReportReadyTemplate,
            new NotificationRecipient(patient.Phone, patient.Email, patient.Id, patient.FullName),
            new Dictionary<string, string> { ["name"] = patient.FullName, ["reference"] = reference },
            channels,
            TimeSpan.FromDays(3));
    }
}
