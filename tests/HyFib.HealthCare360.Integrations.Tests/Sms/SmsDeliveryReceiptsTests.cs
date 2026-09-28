using HyFib.HealthCare360.Integrations.Sms;

namespace HyFib.HealthCare360.Integrations.Tests.Sms;

public sealed class SmsDeliveryReceiptsTests
{
    // DeliveryOutcome is internal, so theories take its name (public test methods cannot expose internal types).
    private static DeliveryOutcome Outcome(string name) => Enum.Parse<DeliveryOutcome>(name);

    [Fact]
    public void Parses_msg91_report_arrays()
    {
        const string json = """
            [
              {"senderId":"NANAHS","requestId":"3263666f6e71373432323538","report":[{"date":"2026-09-25 10:15:02","number":"919876543210","status":"1","desc":"DELIVERED"}],"userId":"1"},
              {"senderId":"NANAHS","requestId":"3263666f6e71373432323539","report":[{"number":"919876543211","status":"16","desc":"REJECTED"}]},
              {"senderId":"NANAHS","requestId":"3263666f6e71373432323540","report":[{"number":"919876543212","status":"8","desc":"SUBMITTED"}]}
            ]
            """;

        var receipts = SmsDeliveryReceipts.ParseMsg91(json);

        receipts.Count.ShouldBe(3);
        receipts[0].ShouldBe(new DeliveryReceipt("3263666f6e71373432323538", DeliveryOutcome.Delivered, "DELIVERED"));
        receipts[1].Outcome.ShouldBe(DeliveryOutcome.Failed);
        receipts[1].Detail.ShouldBe("REJECTED");
        receipts[2].Outcome.ShouldBe(DeliveryOutcome.Pending);
    }

    [Fact]
    public void Parses_msg91_payloads_wrapped_in_data_or_double_encoded()
    {
        SmsDeliveryReceipts.ParseMsg91("""{"data":[{"requestId":"r1","report":[{"status":"1","desc":"DELIVERED"}]}]}""")
            .ShouldHaveSingleItem().Outcome.ShouldBe(DeliveryOutcome.Delivered);
        SmsDeliveryReceipts.ParseMsg91("""{"data":"[{\"requestId\":\"r2\",\"report\":[{\"status\":\"2\",\"desc\":\"FAILED\"}]}]"}""")
            .ShouldHaveSingleItem().Outcome.ShouldBe(DeliveryOutcome.Failed);
        SmsDeliveryReceipts.ParseMsg91("not json").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("1", "DELIVERED", nameof(DeliveryOutcome.Delivered))]
    [InlineData("2", "UNDELIVERED", nameof(DeliveryOutcome.Failed))]
    [InlineData("9", "NDNC", nameof(DeliveryOutcome.Failed))]
    [InlineData("17", "BLOCKED", nameof(DeliveryOutcome.Failed))]
    [InlineData("8", null, nameof(DeliveryOutcome.Pending))]
    [InlineData(null, null, nameof(DeliveryOutcome.Pending))]
    [InlineData("26", null, nameof(DeliveryOutcome.Failed))]
    public void Maps_msg91_status_codes(string? status, string? description, string expected) =>
        SmsDeliveryReceipts.Msg91Outcome(status, description).ShouldBe(Outcome(expected));

    [Theory]
    [InlineData("delivered", nameof(DeliveryOutcome.Delivered))]
    [InlineData("undelivered", nameof(DeliveryOutcome.Failed))]
    [InlineData("failed", nameof(DeliveryOutcome.Failed))]
    [InlineData("sent", nameof(DeliveryOutcome.Pending))]
    [InlineData("queued", nameof(DeliveryOutcome.Pending))]
    public void Maps_twilio_statuses(string status, string expected)
    {
        var receipt = SmsDeliveryReceipts.ParseTwilio(new Dictionary<string, string> { ["MessageSid"] = "SM1", ["MessageStatus"] = status, ["ErrorCode"] = "30003" });

        receipt.ShouldNotBeNull().Outcome.ShouldBe(Outcome(expected));
        if (Outcome(expected) == DeliveryOutcome.Failed)
        {
            receipt.Detail!.ShouldContain("30003");
        }
    }

    [Fact]
    public void Twilio_callbacks_without_a_sid_are_ignored() =>
        SmsDeliveryReceipts.ParseTwilio(new Dictionary<string, string> { ["MessageStatus"] = "delivered" }).ShouldBeNull();

    [Theory]
    [InlineData("msgid", "DELIVRD", nameof(DeliveryOutcome.Delivered))]
    [InlineData("messageId", "failed", nameof(DeliveryOutcome.Failed))]
    [InlineData("id", "submitted", nameof(DeliveryOutcome.Pending))]
    public void Maps_generic_gateway_fields(string idField, string status, string expected) =>
        SmsDeliveryReceipts.ParseGeneric(new Dictionary<string, string> { [idField] = "abc-1", ["status"] = status })
            .ShouldNotBeNull().ShouldBe(new DeliveryReceipt("abc-1", Outcome(expected), status.ToLowerInvariant()));
}
