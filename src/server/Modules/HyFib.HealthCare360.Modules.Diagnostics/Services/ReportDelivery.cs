using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Tells the patient that a report is ready (LIS-07): template <c>lab.report_ready</c> over SMS and WhatsApp, plus e-mail
/// when the patient has an address. The message is queued in the release transaction and sent through the outbox, so
/// it goes out even if the internet is down at the moment of release. The report's PDF goes with the e-mail, and with
/// the WhatsApp message when its template has a document header (setting <see cref="SendPdfSetting"/>, on by default);
/// the SMS only says where to find it. Report content is never sent as message text. A record in a protected category
/// gets no SMS or WhatsApp when another patient shares its number (<see cref="SharedNumbers"/>): the report is given in
/// person, or by e-mail to the patient's own address.
/// </summary>
internal sealed class ReportDelivery(DiagnosticsLookups lookups, HospitalDbContext db, INotificationService notifications, ISettingsService settings)
{
    public const string ReportReadyTemplate = "lab.report_ready";

    /// <summary>Send the report's PDF with the "report ready" e-mail and WhatsApp message (bool, default true).</summary>
    public const string SendPdfSetting = "diagnostics.reports.sendPdf";

    /// <param name="document">The report's PDF to send with the message, or null for a notice only.</param>
    public async Task QueueReportReadyAsync(Guid patientId, string reference, NotificationDocument? document, Guid? facilityId, CancellationToken ct)
    {
        var patient = await lookups.PatientAsync(patientId, ct);

        // Deceased patients' contacts are not messaged; the family collects reports at the counter.
        if (patient is null || patient.IsDeceased)
        {
            return;
        }

        var phone = patient.Phone is not null && !await SharedNumbers.HiddenFromNumberAsync(db, patient.Id, ct) ? patient.Phone : null;
        if (phone is null && patient.Email is null)
        {
            return;
        }

        var channels = new List<NotificationChannel>();
        if (phone is not null)
        {
            channels.Add(NotificationChannel.Sms);
            channels.Add(NotificationChannel.WhatsApp);
        }

        if (patient.Email is not null)
        {
            channels.Add(NotificationChannel.Email);
        }

        var sendPdf = document is not null && (await settings.GetAsync<bool?>(SendPdfSetting, facilityId, ct) ?? true);
        notifications.Queue(
            ReportReadyTemplate,
            NotificationRecipient.Patient(phone, patient.Email, patient.Id, patient.FullName, patient.PreferredLanguage),
            new Dictionary<string, string> { ["name"] = patient.FullName, ["reference"] = reference },
            channels,
            TimeSpan.FromDays(3),
            facilityId,
            sendPdf ? document : null);
    }
}
