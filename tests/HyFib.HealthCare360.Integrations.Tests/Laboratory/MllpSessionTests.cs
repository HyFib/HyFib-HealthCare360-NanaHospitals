using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Hl7;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class MllpSessionTests
{
    private readonly ILabMessageProcessor _processor = Substitute.For<ILabMessageProcessor>();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 25, 5, 0, 16, TimeSpan.Zero));

    public MllpSessionTests()
    {
        _processor.SubmitResultsAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(LabSubmitOutcome.Ok);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MllpSession Session(int maxMessageBytes = 64 * 1024) =>
        new(LabSamples.Hl7Instrument, LabSamples.Options(maxMessageBytes), _processor, _clock, NullLogger.Instance);

    /// <summary>Arguments of every <see cref="ILabMessageProcessor.LogAsync"/> call for one operation.</summary>
    private List<object?[]> Logged(string operation) =>
        _processor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILabMessageProcessor.LogAsync) && (string?)call.GetArguments()[1] == operation)
            .Select(call => call.GetArguments())
            .ToList();

    [Fact]
    public async Task Stores_results_and_acknowledges_with_aa()
    {
        var ack = await Session().HandleAsync(LabSamples.HematologyOru, Ct);

        ack.ShouldStartWith(@"MSH|^~\&|HC360|NanaHospitals|BC-5380|Mindray|20260925103016||ACK^R01^ACK|HC");
        ack.ShouldEndWith("\rMSA|AA|MSG00042\r");
        await _processor.Received(1).SubmitResultsAsync(
            Arg.Is(LabSamples.Hl7Instrument),
            Arg.Is<IReadOnlyList<InstrumentResultMessage>>(m => m.Count == 1 && m[0].SampleBarcode == "SMP-26090042" && m[0].Results.Count == 3),
            Arg.Is(LabSamples.HematologyOru),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Answers_ae_when_the_lis_refuses_so_the_analyser_keeps_the_results()
    {
        _processor.SubmitResultsAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(new LabSubmitOutcome(false, "LIS not available"));

        (await Session().HandleAsync(LabSamples.HematologyOru, Ct)).ShouldEndWith("\rMSA|AE|MSG00042|LIS not available\r");
    }

    [Fact]
    public async Task Rejects_messages_that_are_not_results()
    {
        const string adt = "MSH|^~\\&|HIS|X|HC360|Nana|20260925||ADT^A01|ADT1|P|2.5.1\rPID|1||UHID1";

        var ack = await Session().HandleAsync(adt, Ct);

        ack.ShouldContain(@"MSA|AR|ADT1|Unsupported message type ADT\S\A01.");
        await _processor.DidNotReceiveWithAnyArgs().SubmitResultsAsync(default!, default!, default!, Ct);
        var logged = Logged("hl7.rejected").ShouldHaveSingleItem();
        logged[2].ShouldBe(false);
        logged[3].ShouldBe(adt);
        logged[5].ShouldBe("Unsupported message type ADT^A01.");
        logged[6].ShouldBe("ADT1");
    }

    [Fact]
    public async Task Rejects_unreadable_text() =>
        (await Session().HandleAsync("garbage", Ct)).ShouldEndWith("\rMSA|AR||The message does not start with an MSH segment.\r");

    [Fact]
    public async Task Answers_ae_when_results_cannot_be_mapped()
    {
        const string noResults = "MSH|^~\\&|LAB|X|HC360|Nana|20260925||ORU^R01|M9|P|2.5.1\rOBR|1||S-1";

        (await Session().HandleAsync(noResults, Ct)).ShouldContain("MSA|AE|M9|The message contains no OBX results.");

        Logged("hl7.results").ShouldHaveSingleItem()[5].ShouldBe("The message contains no OBX results.");
    }

    [Fact]
    public async Task Answers_every_framed_message_with_one_framed_ack()
    {
        var pipe = new DuplexPipe();
        var run = Session().RunAsync(pipe.Server, Ct);
        var framed = Mllp.Frame(Encoding.Latin1.GetBytes(LabSamples.HematologyOru));

        // TCP delivers the message in arbitrary pieces.
        await pipe.Client.WriteAsync(framed.AsMemory(0, 100), Ct);
        await pipe.Client.WriteAsync(framed.AsMemory(100), Ct);
        var ack = await pipe.ReadUntilAsync(b => b.Count >= 2 && b[^2] == Mllp.EndBlock && b[^1] == Mllp.CarriageReturn, Ct);

        ack[0].ShouldBe(Mllp.StartBlock);
        Encoding.Latin1.GetString(ack, 1, ack.Length - 3).ShouldEndWith("\rMSA|AA|MSG00042\r");
        await pipe.CloseClientAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Closes_the_connection_when_a_message_exceeds_the_limit()
    {
        var pipe = new DuplexPipe();
        var run = Session(maxMessageBytes: 64).RunAsync(pipe.Server, Ct);

        await pipe.Client.WriteAsync(Mllp.Frame(Encoding.Latin1.GetBytes(LabSamples.HematologyOru)), Ct);
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var logged = Logged("hl7.rejected").ShouldHaveSingleItem();
        logged[3].ShouldBe("(message too large)");
        ((string)logged[5]!).ShouldContain("limit");
        await _processor.DidNotReceiveWithAnyArgs().SubmitResultsAsync(default!, default!, default!, Ct);
    }
}
