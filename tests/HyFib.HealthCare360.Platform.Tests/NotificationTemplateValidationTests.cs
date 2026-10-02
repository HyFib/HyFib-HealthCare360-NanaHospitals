using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Notification template saves are checked before they reach the database: an over-long DLT id used to fail as a server
/// error instead of telling the administrator what to fix.
/// </summary>
public sealed class NotificationTemplateValidationTests
{
    private readonly SaveNotificationTemplateRequestValidator _validator = new();

    [Fact]
    public void A_registered_sms_template_is_accepted() =>
        _validator.Validate(Sms() with { DltTemplateId = "1107160000000012345" }).IsValid.ShouldBeTrue();

    [Theory]
    [InlineData("12345")]
    [InlineData("11071600000000123456789012345")]
    [InlineData("1107-1600-0000")]
    public void A_dlt_template_id_is_10_to_25_digits(string dltTemplateId) =>
        _validator.Validate(Sms() with { DltTemplateId = dltTemplateId }).Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("DltTemplateId");

    [Fact]
    public void An_sms_is_at_most_1000_characters() =>
        _validator.Validate(Sms() with { Body = new string('a', 1001) }).Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Body");

    [Fact]
    public void A_whatsapp_template_name_is_the_approved_lower_case_name()
    {
        var request = Sms() with { Channel = NotificationChannel.WhatsApp, WhatsAppTemplateName = "Lab Report Ready" };

        _validator.Validate(request).Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("WhatsAppTemplateName");
        _validator.Validate(request with { WhatsAppTemplateName = "lab_report_ready" }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_template_needs_a_message() =>
        _validator.Validate(Sms() with { Body = "" }).Errors.ShouldHaveSingleItem().ErrorMessage.ShouldBe("Write the message.");

    private static SaveNotificationTemplateRequest Sms() => new(
        "lab.report_ready", NotificationChannel.Sms, "en", null, null, "Dear {{name}}, your lab report is ready. - Nana Hospitals", null, null,
        "Sent when a lab report is released", true, null);
}
