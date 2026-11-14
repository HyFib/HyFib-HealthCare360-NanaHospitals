using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// "In use" on a notification template: when no stored version that fits a message is in use, the message is not sent
/// on that channel; with none stored, the built-in text goes. Sign-in codes can't be switched off.
/// </summary>
public sealed class TemplateSwitchTests
{
    [Fact]
    public void A_message_with_no_version_in_use_is_off() =>
        TemplateSwitch.IsOff("lab.report_ready", NotificationChannel.WhatsApp, [false, false]).ShouldBeTrue();

    [Fact]
    public void One_version_in_use_is_enough() =>
        TemplateSwitch.IsOff("lab.report_ready", NotificationChannel.WhatsApp, [false, true]).ShouldBeFalse();

    [Fact]
    public void A_message_with_no_stored_version_uses_the_built_in_text() =>
        TemplateSwitch.IsOff("lab.report_ready", NotificationChannel.WhatsApp, []).ShouldBeFalse();

    [Fact]
    public void Sign_in_codes_always_go_so_that_patients_can_sign_in() =>
        TemplateSwitch.IsOff("auth.otp", NotificationChannel.Sms, [false]).ShouldBeFalse();

    [Fact]
    public void A_built_in_whatsapp_template_seeded_not_in_use_is_off_until_it_is_stored_and_switched_on()
    {
        TemplateSwitch.IsOff("visit.token", NotificationChannel.WhatsApp, []).ShouldBeTrue("not stored yet, and it starts not in use");
        TemplateSwitch.IsOff("visit.token", NotificationChannel.WhatsApp, [true]).ShouldBeFalse("switched on once Meta approved it");
        TemplateSwitch.IsOff("visit.token", NotificationChannel.Sms, []).ShouldBeFalse("its SMS is in use from the start");
    }

    [Fact]
    public void Only_built_in_whatsapp_templates_start_not_in_use()
    {
        DefaultNotificationTemplates.SeededNotInUse.ShouldAllBe(t => t.Channel == NotificationChannel.WhatsApp);
        DefaultNotificationTemplates.SeededNotInUse.ShouldAllBe(t => DefaultNotificationTemplates.All.Any(d => d.Key == t.Key && d.Channel == t.Channel));
        DefaultNotificationTemplates.SeededNotInUse.Select(t => t.Key).ShouldBe(
            ["visit.token", "bill.receipt", "admission.admitted", "discharge.completed", "discharge.summary", "feedback.request", "followup.reminder",
                "patients.verify_code"], ignoreOrder: true);
    }

    [Fact]
    public void Only_the_discharge_summary_template_is_seeded_with_a_document_header()
    {
        DefaultNotificationTemplates.SeededWithDocumentHeader.ShouldAllBe(t => t.Channel == NotificationChannel.WhatsApp);
        DefaultNotificationTemplates.SeededWithDocumentHeader.ShouldAllBe(t => DefaultNotificationTemplates.All.Any(d => d.Key == t.Key && d.Channel == t.Channel));
        DefaultNotificationTemplates.SeededWithDocumentHeader.Select(t => t.Key).ShouldBe(["discharge.summary"]);
    }

    [Theory]
    [InlineData(NotificationChannel.Sms, "SMS")]
    [InlineData(NotificationChannel.WhatsApp, "WhatsApp")]
    [InlineData(NotificationChannel.Email, "e-mail")]
    public void The_log_says_which_template_is_off_and_where_to_switch_it_on(NotificationChannel channel, string name) =>
        TemplateSwitch.NotInUseReason(channel).ShouldBe(
            $"Not sent: this message's {name} template is set to \"Not in use\" (Administration › Notification templates). Switch it on there to send it.");
}
