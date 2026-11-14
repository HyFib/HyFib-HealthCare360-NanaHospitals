using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Services;
using HyFib.HealthCare360.Modules.VirtualCare.Services;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Every channel a message is queued on has a template stored for all facilities in English, on a database the real
/// seeders made: the built-in templates, and those each module seeds. Without one the delivery handler can't render the
/// message, and the outbox tries it again until it expires, so a channel added to a message without its template fails
/// here instead of in a hospital. The channels are each sender's own lists, so this follows them as they change.
/// </summary>
public sealed class MessageTemplateCoverageTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly (string Key, IReadOnlyList<NotificationChannel> Channels)[] Messages =
    [
        ("visit.token", VisitService.TokenChannels),
        ("admission.admitted", AdmissionService.AdmittedChannels),
        (DischargeService.DischargedTemplate, [.. DischargeService.DischargedSmsChannels, .. DischargeService.DischargedWhatsAppChannels]),
        (DischargeService.DischargeSummaryTemplate, DischargeService.DischargedWhatsAppChannels),
        ("bill.receipt", ReceiptService.ReceiptChannels(billOnWhatsApp: false)),
        (BillSharing.Template, [NotificationChannel.WhatsApp]),
        (FollowUpReminders.Template, FollowUpReminders.Channels),
        (FeedbackInvitationService.RequestTemplate, FeedbackInvitationService.Channels),
        (FeedbackService.ComplaintAcknowledgedTemplate, FeedbackService.ComplaintAcknowledgedChannels),
        (AppointmentNotifier.ConfirmedTemplate, AppointmentNotifier.PatientChannels),
        (AppointmentNotifier.ReminderTemplate, AppointmentNotifier.PatientChannels),
        (AppointmentNotifier.CancelledTemplate, AppointmentNotifier.PatientChannels),
        (AppointmentNotifier.WaitlistTemplate, AppointmentNotifier.PatientChannels),
        (StrandedPatientNotifier.Template, [NotificationChannel.Sms, NotificationChannel.WhatsApp]),
        (TelehealthLinkSender.Template, TelehealthLinkSender.Channels),
        (RpmAlertService.CareTeamTemplate, RpmAlertService.Channels),
        (RpmAlertService.DeviceSilentTemplate, RpmAlertService.Channels),
        (SharePrescriptionAfterTeleconsultation.Template, SharePrescriptionAfterTeleconsultation.Channels),
        ("prescription.shared", [NotificationChannel.WhatsApp]),
        ("lab.report_ready", [NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Email]),
        ("payment.link", [NotificationChannel.Sms, NotificationChannel.WhatsApp]),
        ("pharmacy.refill_reminder", [NotificationChannel.Sms, NotificationChannel.WhatsApp]),
        ("pharmacy.refill_ready", [NotificationChannel.Sms, NotificationChannel.WhatsApp]),
        ("portal.link", [NotificationChannel.Sms, NotificationChannel.WhatsApp]),
        ("system.alert", [NotificationChannel.Sms, NotificationChannel.WhatsApp]),
        (PatientPhoneVerificationService.Template, PatientPhoneVerificationService.Channels),
    ];

    [Fact]
    public async Task Every_channel_a_message_is_queued_on_has_a_template_for_all_facilities_in_english()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var stored = (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationTemplate>().AsNoTracking()
                .Where(t => t.FacilityId == null && t.Language == "en")
                .Select(t => new { t.Key, t.Channel })
                .ToListAsync(Ct))
            .Select(t => (t.Key, t.Channel))
            .ToHashSet();

        var missing = Messages.SelectMany(m => m.Channels.Select(c => (m.Key, Channel: c))).Where(m => !stored.Contains(m)).ToList();

        missing.ShouldBeEmpty("Seed a template for each of these messages and channels (DefaultNotificationTemplates, or the module's seeder): "
                              + string.Join(", ", missing.Select(m => $"{m.Key} ({m.Channel})")));
    }
}
