using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Hl7;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class Hl7OrderMessagesTests
{
    private static readonly DateTimeOffset LocalNow = new(2026, 9, 25, 10, 30, 0, TimeSpan.FromHours(5.5));

    private static readonly InstrumentOrder Order = new("000000123", "Ravi|Sharma", "F", new DateOnly(1990, 3, 1), ["GLU", "CREA"])
    {
        PatientId = "NH-UH-26001",
        Priority = OrderPriority.Urgent,
        CollectedAt = new DateTimeOffset(2026, 9, 25, 4, 0, 0, TimeSpan.Zero),
        SpecimenType = "Serum",
    };

    [Fact]
    public void Builds_an_oml_o21_with_one_order_group_per_test()
    {
        var message = Hl7OrderMessages.Build(Order, InstrumentOrderAction.New, Hl7OrderMessages.Oml, "COBAS", "HC123", LocalNow, LabSamples.India);

        message.ShouldEndWith("\r");
        var segments = message.TrimEnd('\r').Split('\r');
        segments.ShouldBe(
        [
            @"MSH|^~\&|HC360|HC360|COBAS||20260925103000||OML^O21^OML_O21|HC123|P|2.5.1",
            @"PID|1||NH-UH-26001^^^HC360^MR||Ravi\F\Sharma||19900301|F",
            "ORC|NW|000000123",
            "TQ1|1||||||||A",
            "OBR|1|000000123||GLU^GLU^99HC360|||20260925093000",
            "SPM|1|000000123||Serum",
            "ORC|NW|000000123",
            "TQ1|1||||||||A",
            "OBR|2|000000123||CREA^CREA^99HC360|||20260925093000",
            "SPM|1|000000123||Serum",
        ]);
    }

    [Fact]
    public void Builds_an_orm_o01_cancellation_for_older_analysers()
    {
        var message = Hl7OrderMessages.Build(Order with { ChannelCodes = ["TSH"] }, InstrumentOrderAction.Cancel, Hl7OrderMessages.Orm, "BS240", "HC9", LocalNow, LabSamples.India);

        var segments = message.TrimEnd('\r').Split('\r');
        segments[0].ShouldBe(@"MSH|^~\&|HC360|HC360|BS240||20260925103000||ORM^O01|HC9|P|2.3.1");
        segments[1].ShouldBe(@"PID|1||NH-UH-26001||Ravi\F\Sharma||19900301|F");
        segments[2].ShouldBe("ORC|CA|000000123");
        segments[3].ShouldBe("OBR|1|000000123||TSH^TSH^99HC360|A||20260925093000||||||||Serum");
        segments.Length.ShouldBe(4);
    }

    [Theory]
    [InlineData(null, null, Hl7OrderMessages.Oml)]
    [InlineData(null, "2.3.1", Hl7OrderMessages.Orm)]
    [InlineData(Hl7OrderMessages.Oml, "2.3.1", Hl7OrderMessages.Oml)]
    [InlineData(Hl7OrderMessages.Orm, "2.5.1", Hl7OrderMessages.Orm)]
    public void Uses_the_configured_message_or_the_analysers_version(string? configured, string? version, string expected) =>
        Hl7OrderMessages.MessageFor(LabSamples.Hl7Instrument with { OrderMessage = configured }, version).ShouldBe(expected);

    [Theory]
    [InlineData(null, true)]
    [InlineData("OML^O21", true)]
    [InlineData("ORM^O01", true)]
    [InlineData("ORU^R01", false)]
    public void Knows_the_order_messages_it_can_send(string? messageType, bool known) => Hl7OrderMessages.IsKnown(messageType).ShouldBe(known);
}
