using HyFib.HealthCare360.Modules.VirtualCare.Seed;
using HyFib.HealthCare360.Modules.VirtualCare.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

/// <summary>
/// TEL-04: after a video consultation the patient hears about the signed prescription once on each channel. Clinical
/// sends <c>prescription.shared</c> on WhatsApp for every out-patient prescription, so VirtualCare adds only the SMS.
/// </summary>
public sealed class PrescriptionNoticeTests
{
    [Fact]
    public void Only_the_sms_is_queued_here_because_clinical_sends_the_whatsapp_message() =>
        SharePrescriptionAfterTeleconsultation.Channels.ShouldBe([NotificationChannel.Sms]);

    [Fact]
    public void Every_channel_it_queues_on_has_a_template()
    {
        // Without a template for the channel the renderer throws, and the message is never sent.
        foreach (var channel in SharePrescriptionAfterTeleconsultation.Channels)
        {
            (DefaultNotificationTemplates.All.Any(t => t.Key == SharePrescriptionAfterTeleconsultation.Template && t.Channel == channel)
             || VirtualCareSeeder.Templates.Any(t => t.Key == SharePrescriptionAfterTeleconsultation.Template && t.Channel == channel))
                .ShouldBeTrue($"No '{SharePrescriptionAfterTeleconsultation.Template}' template for {channel}.");
        }
    }
}
