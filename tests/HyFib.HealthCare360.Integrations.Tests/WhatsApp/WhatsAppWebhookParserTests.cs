using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Sms;
using HyFib.HealthCare360.Integrations.WhatsApp;

namespace HyFib.HealthCare360.Integrations.Tests.WhatsApp;

public sealed class WhatsAppWebhookParserTests
{
    private const string Payload = """
        {
          "object": "whatsapp_business_account",
          "entry": [{
            "id": "102290129340398",
            "changes": [{
              "field": "messages",
              "value": {
                "messaging_product": "whatsapp",
                "metadata": { "display_phone_number": "919000000000", "phone_number_id": "106540352242922" },
                "contacts": [{ "profile": { "name": "Ravi Kumar" }, "wa_id": "919876543210" }],
                "messages": [
                  { "from": "919876543210", "id": "wamid.IN1", "timestamp": "1758775200", "type": "text", "text": { "body": "STOP" } },
                  { "from": "919876543210", "id": "wamid.IN2", "timestamp": "1758775260", "type": "button", "button": { "text": "Confirm", "payload": "CONFIRM" },
                    "context": { "from": "919000000000", "id": "wamid.OUT7" } },
                  { "from": "919876543210", "id": "wamid.IN3", "timestamp": "1758775300", "type": "document", "document": { "id": "media-55", "caption": "old report", "mime_type": "application/pdf" } }
                ],
                "statuses": [
                  { "id": "wamid.OUT1", "status": "delivered", "timestamp": "1758775100", "recipient_id": "919876543210" },
                  { "id": "wamid.OUT2", "status": "failed", "timestamp": "1758775110", "recipient_id": "919876543211",
                    "errors": [{ "code": 131026, "title": "Message undeliverable" }] },
                  { "id": "wamid.OUT3", "status": "sent", "timestamp": "1758775120", "recipient_id": "919876543212" }
                ]
              }
            }]
          }]
        }
        """;

    [Fact]
    public void Reads_delivery_statuses()
    {
        var batch = WhatsAppWebhookParser.Parse(Payload);

        batch.Statuses.Count.ShouldBe(3);
        batch.Statuses[0].ShouldBe(new DeliveryReceipt("wamid.OUT1", DeliveryOutcome.Delivered, "delivered"));
        batch.Statuses[1].Outcome.ShouldBe(DeliveryOutcome.Failed);
        batch.Statuses[1].Detail.ShouldBe("WhatsApp 131026 Message undeliverable");
        batch.Statuses[2].Outcome.ShouldBe(DeliveryOutcome.Pending);
    }

    [Fact]
    public void Reads_inbound_messages_with_sender_profile_and_context()
    {
        var batch = WhatsAppWebhookParser.Parse(Payload);

        batch.Messages.Count.ShouldBe(3);
        var text = batch.Messages[0];
        text.From.ShouldBe("919876543210");
        text.ProfileName.ShouldBe("Ravi Kumar");
        text.Text.ShouldBe("STOP");
        text.PhoneNumberId.ShouldBe("106540352242922");
        text.SentAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1758775200));
        batch.Messages[1].Text.ShouldBe("Confirm");
        batch.Messages[1].ContextMessageId.ShouldBe("wamid.OUT7");
        batch.Messages[2].Type.ShouldBe("document");
        batch.Messages[2].MediaId.ShouldBe("media-55");
        batch.Messages[2].Text.ShouldBe("old report");
    }

    [Theory]
    [InlineData("STOP", WhatsAppKeyword.OptOut)]
    [InlineData(" unsubscribe. ", WhatsAppKeyword.OptOut)]
    [InlineData("Start", WhatsAppKeyword.OptIn)]
    [InlineData("please stop sending reminders on sunday", WhatsAppKeyword.None)]
    [InlineData(null, WhatsAppKeyword.None)]
    public void Classifies_only_exact_opt_in_and_opt_out_keywords(string? text, WhatsAppKeyword expected) =>
        WhatsAppWebhookParser.Classify(text).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"object":"page","entry":[]}""")]
    public void Unrelated_or_malformed_payloads_yield_nothing(string payload)
    {
        var batch = WhatsAppWebhookParser.Parse(payload);

        batch.Statuses.ShouldBeEmpty();
        batch.Messages.ShouldBeEmpty();
    }
}
