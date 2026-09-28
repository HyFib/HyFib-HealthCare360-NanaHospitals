using HyFib.HealthCare360.Integrations.Laboratory.Hl7;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class Hl7AckTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 30, 16, TimeSpan.FromHours(5.5));

    private static Hl7Message Oru()
    {
        Hl7Message.TryParse(LabSamples.HematologyOru, out var message, out _).ShouldBeTrue();
        return message;
    }

    [Fact]
    public void Accepts_by_swapping_sender_and_receiver_and_echoing_the_control_id() =>
        Hl7Ack.Build(Oru(), Hl7Ack.Accept, null, "HC0000000000000001", Now).ShouldBe(
            @"MSH|^~\&|HC360|NanaHospitals|BC-5380|Mindray|20260925103016||ACK^R01^ACK|HC0000000000000001|P|2.5.1" + "\r" +
            "MSA|AA|MSG00042\r");

    [Fact]
    public void Application_errors_carry_an_escaped_and_bounded_reason()
    {
        var ack = Hl7Ack.Build(Oru(), Hl7Ack.Error, "LIS not available | retry " + new string('x', 200), "HC1", Now);

        var msa = ack.Split('\r')[1];
        msa.ShouldStartWith(@"MSA|AE|MSG00042|LIS not available \F\ retry xxx");
        msa.Length.ShouldBeLessThan(100);
        Hl7Message.TryParse(ack, out var parsed, out _).ShouldBeTrue();
        parsed.Segments[1].Value(3).ShouldStartWith("LIS not available | retry");
    }

    [Fact]
    public void Rejects_unreadable_messages_with_default_header_values()
    {
        var ack = Hl7Ack.Build(null, Hl7Ack.Reject, "The message does not start with an MSH segment.", "HC2", Now);

        ack.ShouldBe(@"MSH|^~\&|HC360||||20260925103016||ACK|HC2|P|2.5.1" + "\rMSA|AR||The message does not start with an MSH segment.\r");
    }

    [Fact]
    public void Keeps_the_senders_processing_id_and_version()
    {
        Hl7Message.TryParse(@"MSH|^~\&|LAB|X|HC360|Nana|20260925||ORU^R01|M1|T|2.3.1", out var message, out _).ShouldBeTrue();

        var header = Hl7Ack.Build(message, Hl7Ack.Accept, null, "HC3", Now).Split('\r')[0];

        header.ShouldEndWith("|HC3|T|2.3.1");
    }

    [Fact]
    public void Control_ids_fit_the_msh_10_length_limit()
    {
        var first = Hl7Ack.NewControlId();
        var second = Hl7Ack.NewControlId();

        first.Length.ShouldBe(20);
        first.ShouldStartWith("HC");
        first.ShouldNotBe(second);
    }
}
