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
        TemplateSwitch.IsOff("lab.report_ready", [false, false]).ShouldBeTrue();

    [Fact]
    public void One_version_in_use_is_enough() =>
        TemplateSwitch.IsOff("lab.report_ready", [false, true]).ShouldBeFalse();

    [Fact]
    public void A_message_with_no_stored_version_uses_the_built_in_text() =>
        TemplateSwitch.IsOff("lab.report_ready", []).ShouldBeFalse();

    [Fact]
    public void Sign_in_codes_always_go_so_that_patients_can_sign_in() =>
        TemplateSwitch.IsOff("auth.otp", [false]).ShouldBeFalse();

    [Theory]
    [InlineData(NotificationChannel.Sms, "SMS")]
    [InlineData(NotificationChannel.WhatsApp, "WhatsApp")]
    [InlineData(NotificationChannel.Email, "e-mail")]
    public void The_log_says_which_template_is_off_and_where_to_switch_it_on(NotificationChannel channel, string name) =>
        TemplateSwitch.NotInUseReason(channel).ShouldBe(
            $"Not sent: this message's {name} template is set to \"Not in use\" (Administration › Notification templates). Switch it on there to send it.");
}
