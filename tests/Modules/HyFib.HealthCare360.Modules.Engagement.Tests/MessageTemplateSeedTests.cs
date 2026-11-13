using HyFib.HealthCare360.Modules.Engagement.Seed;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

/// <summary>
/// The templates Engagement seeds: every channel its messages go on has one, and the WhatsApp ones Meta must approve first
/// are stored "Not in use", so nothing goes on WhatsApp before the hospital switches them on.
/// </summary>
public sealed class MessageTemplateSeedTests
{
    [Fact]
    public void The_complaint_acknowledgement_has_a_template_on_every_channel_it_goes_on() =>
        FeedbackService.ComplaintAcknowledgedChannels.ShouldAllBe(c =>
            EngagementSeeder.Templates.Any(t => t.Key == FeedbackService.ComplaintAcknowledgedTemplate && t.Channel == c));

    [Fact]
    public void Only_seeded_whatsapp_templates_start_not_in_use()
    {
        EngagementSeeder.SeededNotInUse.ShouldAllBe(t => t.Channel == NotificationChannel.WhatsApp);
        EngagementSeeder.SeededNotInUse.ShouldAllBe(t => EngagementSeeder.Templates.Any(s => s.Key == t.Key && s.Channel == t.Channel));
        EngagementSeeder.SeededNotInUse.ShouldNotContain(("campaign.message", NotificationChannel.WhatsApp), "campaigns on WhatsApp were in use before");
    }
}
