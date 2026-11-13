using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The placeholders of a message's template body with the values they took, for WhatsApp templates registered with them
/// as parameters. The pass that renders the text lists them, so the two agree: in order, a placeholder used twice
/// listed twice, the hospital's and the recipient's names where the message gives none, and a placeholder the message
/// has no value for empty, as it is in the text.
/// </summary>
public sealed class NotificationPlaceholderTests
{
    [Fact]
    public void Lists_each_placeholder_of_the_body_in_order_with_the_value_it_took()
    {
        var message = NotificationTemplateRenderer.Fill(
            Delivery(new() { ["amount"] = "1,200.00", ["billNumber"] = "NHOB26000042", ["receiptNumber"] = "NHRC260000123" }), null,
            "Dear {{name}}, we have received Rs.{{amount}} for {{billNumber}} (receipt {{ receiptNumber }}). Quote {{billNumber}} for your claim.",
            "Nana Hospitals");

        message.Body.ShouldBe(
            "Dear Asha Rao, we have received Rs.1,200.00 for NHOB26000042 (receipt NHRC260000123). Quote NHOB26000042 for your claim.");
        message.Placeholders.ShouldBe([
            new TemplatePlaceholder("name", "Asha Rao"),
            new TemplatePlaceholder("amount", "1,200.00"),
            new TemplatePlaceholder("billNumber", "NHOB26000042"),
            new TemplatePlaceholder("receiptNumber", "NHRC260000123"),
            new TemplatePlaceholder("billNumber", "NHOB26000042"),
        ]);
    }

    [Fact]
    public void Fills_in_the_hospital_and_the_recipient_where_the_message_gives_no_value_for_them()
    {
        var message = NotificationTemplateRenderer.Fill(
            Delivery(new() { ["token"] = "OPD-17" }), null, "{{hospital}}: {{name}}, your token is {{token}}.", "Nana Hospitals");

        message.Body.ShouldBe("Nana Hospitals: Asha Rao, your token is OPD-17.");
        message.Placeholders.ShouldBe([
            new TemplatePlaceholder("hospital", "Nana Hospitals"),
            new TemplatePlaceholder("name", "Asha Rao"),
            new TemplatePlaceholder("token", "OPD-17"),
        ]);
    }

    [Fact]
    public void The_messages_own_values_come_before_the_defaults_whatever_their_letter_case()
    {
        var message = NotificationTemplateRenderer.Fill(
            Delivery(new() { ["Hospital"] = "Nana Hospitals, Theni", ["name"] = "Ravi Kumar" }), null, "{{hospital}}: {{name}}", null);

        message.Placeholders.ShouldBe([
            new TemplatePlaceholder("hospital", "Nana Hospitals, Theni"),
            new TemplatePlaceholder("name", "Ravi Kumar"),
        ]);
    }

    [Fact]
    public void Without_the_facilitys_name_the_hospital_is_Hospital_and_without_a_recipients_name_the_name_is_empty()
    {
        var message = NotificationTemplateRenderer.Fill(Delivery([], name: null), null, "{{hospital}}: {{name}}", null);

        message.Body.ShouldBe("Hospital: ");
        message.Placeholders.ShouldBe([new TemplatePlaceholder("hospital", "Hospital"), new TemplatePlaceholder("name", "")]);
    }

    [Fact]
    public void A_placeholder_the_message_has_no_value_for_is_empty_as_in_the_text()
    {
        var message = NotificationTemplateRenderer.Fill(Delivery([]), null, "Your report {{reference}} is ready.", "Nana Hospitals");

        message.Body.ShouldBe("Your report  is ready.");
        message.Placeholders.ShouldBe([new TemplatePlaceholder("reference", "")]);
    }

    [Fact]
    public void A_body_without_placeholders_lists_none() =>
        NotificationTemplateRenderer.Fill(Delivery([]), null, "The clinic is closed today.", "Nana Hospitals").Placeholders.ShouldBeEmpty();

    private static NotificationDelivery Delivery(Dictionary<string, string> values, string? name = "Asha Rao") =>
        new(Guid.CreateVersion7(), "bill.receipt", NotificationChannel.WhatsApp, "+919876543210", name, "en", values, null);
}
