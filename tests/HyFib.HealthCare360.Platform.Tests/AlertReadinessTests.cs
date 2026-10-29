using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// System health's warning when a critical warning would not reach IT on-call by phone (audit P2): critical where SMS is set
/// up and yet every alert SMS fails unseen, a warning where nobody set it up or the operators may drop it, and nothing when
/// it goes.
/// </summary>
public sealed class AlertReadinessTests
{
    private const string Phone = "+919800000142";
    private const string DltId = "1107161234567890123";

    [Fact]
    public void An_on_call_number_with_msg91_and_a_registered_template_is_ready() =>
        AlertReadiness.Warning(Route()).ShouldBeNull();

    [Fact]
    public void No_on_call_number_is_a_warning_naming_the_setting()
    {
        var warning = AlertReadiness.Warning(Route() with { OnCallPhone = " " }).ShouldNotBeNull();

        (warning.Code, warning.Severity).ShouldBe(("alerts.not_ready", "warning"));
        warning.Message.ShouldContain("alerts.oncallPhone");
    }

    [Theory]
    [InlineData("9800")]
    [InlineData("+91 98000")]
    [InlineData("IT desk")]
    public void An_on_call_number_that_is_not_a_phone_number_skips_every_alert_and_is_critical(string onCall)
    {
        var warning = AlertReadiness.Warning(Route() with { OnCallPhone = onCall, WhatsAppToOnCall = true }).ShouldNotBeNull();

        warning.Severity.ShouldBe("critical", "a number was set, and every alert to it is skipped unseen, on WhatsApp too");
        warning.Message.ShouldContain("alerts.oncallPhone");
        warning.Message.ShouldContain("not a phone number");
    }

    [Theory]
    [InlineData("98000 00142")]
    [InlineData("098000-00142")]
    [InlineData("919800000142")]
    public void An_on_call_number_written_another_way_is_one_the_alerts_send_to(string onCall) =>
        AlertReadiness.Warning(Route() with { OnCallPhone = onCall }).ShouldBeNull();

    [Fact]
    public void Msg91_without_a_dlt_template_id_fails_every_alert_and_is_critical()
    {
        var warning = AlertReadiness.Warning(Route() with { AlertDltTemplateId = null }).ShouldNotBeNull();

        warning.Severity.ShouldBe("critical", "the hospital set SMS up, and every alert SMS fails unseen");
        warning.Message.ShouldContain("MSG91 requires");
        warning.Message.ShouldContain("Administration › Message templates");
    }

    [Fact]
    public void No_alert_template_fails_every_alert_and_is_critical() =>
        AlertReadiness.Warning(Route() with { AlertSmsTemplate = false }).ShouldNotBeNull().Severity.ShouldBe("critical");

    [Fact]
    public void A_failing_sms_is_only_a_warning_while_whatsapp_still_carries_the_alerts()
    {
        var warning = AlertReadiness.Warning(Route() with { AlertDltTemplateId = null, WhatsAppToOnCall = true }).ShouldNotBeNull();

        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldContain("on WhatsApp only");
    }

    [Theory]
    [InlineData("generic")]
    [InlineData("GENERIC")]
    public void Another_indian_gateway_without_a_dlt_template_id_may_be_dropped(string provider)
    {
        var warning = AlertReadiness.Warning(Route() with { SmsProvider = provider.ToLowerInvariant(), AlertDltTemplateId = null }).ShouldNotBeNull();

        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldContain("Indian operators drop SMS");
    }

    [Fact]
    public void Twilio_takes_no_dlt_template_id_from_hc360() =>
        AlertReadiness.Warning(Route() with { SmsProvider = "twilio", AlertDltTemplateId = null }).ShouldBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("simulator")]
    public void No_sms_and_no_whatsapp_is_a_warning(string? provider)
    {
        var warning = AlertReadiness.Warning(Route() with { SmsProvider = provider }).ShouldNotBeNull();

        warning.Severity.ShouldBe("warning", "nobody set the alerts up: nothing fails unseen");
        warning.Message.ShouldContain("SMS is not set up");
    }

    [Fact]
    public void Whatsapp_alone_is_the_hospitals_choice() =>
        AlertReadiness.Warning(Route() with { SmsProvider = null, WhatsAppToOnCall = true }).ShouldBeNull();

    private static AlertRoute Route() => new(Phone, "msg91", AlertSmsTemplate: true, DltId, WhatsAppToOnCall: false);
}
