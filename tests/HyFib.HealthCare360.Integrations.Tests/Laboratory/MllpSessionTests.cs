using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Hl7;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class MllpSessionTests
{
    private const string Qbp = "MSH|^~\\&|cobas|Roche|HC360|NanaHospitals|20260925110000||QBP^Q11^QBP_Q11|Q00001|P|2.5.1\r"
                               + "QPD|WOS^Work Order Step^IHE_LABTF|TAG01|SMP-1\rRCP|I||R";

    private const string Qry = "MSH|^~\\&|BS-240|Mindray|HC360|NanaHospitals|20260925110000||QRY^Q02|Q00002|P|2.3.1\r"
                               + "QRD|20260925110000|R|D|QRY7|||RD|SMP-1|OTH|||T\rQRF||||||RCT|COR|ALL||";

    private static readonly InstrumentOrder Order = new("SMP-1", "Ravi Sharma", "M", new DateOnly(1980, 1, 15), ["GLU", "CREA"]) { PatientId = "NH-UH-1" };

    private static readonly IReadOnlyList<LabDownload> NoDownloads = [];

    private readonly ILabMessageProcessor _processor = Substitute.For<ILabMessageProcessor>();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 25, 5, 0, 16, TimeSpan.Zero));

    public MllpSessionTests()
    {
        _processor.SubmitResultsAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(LabSubmitOutcome.Ok);
        _processor.QueryOrdersAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(HostQueryAnswer.Answer([Order]));
        _processor.NextDownloadsAsync(default!, default, Ct).ReturnsForAnyArgs(NoDownloads);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MllpSession Session(int maxMessageBytes = 64 * 1024, LabInstrumentSettings? instrument = null) =>
        new(instrument ?? LabSamples.Hl7Instrument, LabSamples.Options(maxMessageBytes), _processor, _clock, NullLogger.Instance);

    /// <summary>Arguments of every <see cref="ILabMessageProcessor.LogAsync"/> call for one operation.</summary>
    private List<object?[]> Logged(string operation) =>
        _processor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILabMessageProcessor.LogAsync) && (string?)call.GetArguments()[1] == operation)
            .Select(call => call.GetArguments())
            .ToList();

    private List<object?[]> Completed() =>
        _processor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILabMessageProcessor.CompleteDownloadsAsync))
            .Select(call => call.GetArguments())
            .ToList();

    private static async Task SendAsync(DuplexPipe pipe, string message)
    {
        await pipe.Client.WriteAsync(Mllp.Frame(Encoding.Latin1.GetBytes(message)), Ct);
        await pipe.Client.FlushAsync(Ct);
    }

    private static async Task<string> ReadMessageAsync(DuplexPipe pipe)
    {
        var framed = await pipe.ReadUntilAsync(b => b.Count >= 2 && b[^2] == Mllp.EndBlock && b[^1] == Mllp.CarriageReturn, Ct);
        framed[0].ShouldBe(Mllp.StartBlock);
        return Encoding.Latin1.GetString(framed, 1, framed.Length - 3);
    }

    private static string ControlId(string message) => message.Split('\r')[0].Split('|')[9];

    private static string Ack(string controlId, string code = "AA", string trigger = "O21") =>
        $"MSH|^~\\&|cobas|Roche|HC360|NanaHospitals|20260925110001||ACK^{trigger}|A{controlId[^6..]}|P|2.5.1\rMSA|{code}|{controlId}";

    [Fact]
    public async Task Stores_results_and_acknowledges_with_aa()
    {
        var ack = await Session().HandleAsync(LabSamples.HematologyOru, Ct);

        ack.ShouldNotBeNull().ShouldStartWith(@"MSH|^~\&|HC360|NanaHospitals|BC-5380|Mindray|20260925103016||ACK^R01^ACK|HC");
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

        (await Session().HandleAsync(LabSamples.HematologyOru, Ct)).ShouldNotBeNull().ShouldEndWith("\rMSA|AE|MSG00042|LIS not available\r");
    }

    [Fact]
    public async Task Rejects_messages_that_are_not_results()
    {
        const string adt = "MSH|^~\\&|HIS|X|HC360|Nana|20260925||ADT^A01|ADT1|P|2.5.1\rPID|1||UHID1";

        var ack = await Session().HandleAsync(adt, Ct);

        ack.ShouldNotBeNull().ShouldContain(@"MSA|AR|ADT1|Unsupported message type ADT\S\A01.");
        await _processor.DidNotReceiveWithAnyArgs().SubmitResultsAsync(default!, default!, default!, Ct);
        var logged = Logged("hl7.rejected").ShouldHaveSingleItem();
        logged[2].ShouldBe(false);
        logged[3].ShouldBe(adt);
        logged[5].ShouldBe("Unsupported message type ADT^A01.");
        logged[6].ShouldBe("ADT1");
    }

    [Fact]
    public async Task Rejects_unreadable_text() =>
        (await Session().HandleAsync("garbage", Ct)).ShouldNotBeNull().ShouldEndWith("\rMSA|AR||The message does not start with an MSH segment.\r");

    [Fact]
    public async Task Answers_ae_when_results_cannot_be_mapped()
    {
        const string noResults = "MSH|^~\\&|LAB|X|HC360|Nana|20260925||ORU^R01|M9|P|2.5.1\rOBR|1||S-1";

        (await Session().HandleAsync(noResults, Ct)).ShouldNotBeNull().ShouldContain("MSA|AE|M9|The message contains no OBX results.");

        Logged("hl7.results").ShouldHaveSingleItem()[5].ShouldBe("The message contains no OBX results.");
    }

    [Fact]
    public async Task Hands_results_without_a_sample_id_to_the_lis_and_acknowledges_them_once_kept()
    {
        // No barcode in SPM-2, SAC-3, OBR-3 or OBR-2: the LIS keeps the results for a person, so the analyser can move on.
        const string unnamed = "MSH|^~\\&|LAB|X|HC360|Nana|20260925||ORU^R01|M10|P|2.5.1\rOBR|1\rOBX|1|NM|GLU||98|mg/dL||N|||F";

        (await Session().HandleAsync(unnamed, Ct)).ShouldNotBeNull().ShouldEndWith("\rMSA|AA|M10\r");

        await _processor.Received(1).SubmitResultsAsync(
            Arg.Is(LabSamples.Hl7Instrument),
            Arg.Is<IReadOnlyList<InstrumentResultMessage>>(m => m.Count == 1 && m[0].SampleBarcode == "" && m[0].Results.Count == 1),
            Arg.Is(unnamed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Never_acknowledges_an_acknowledgement()
    {
        (await Session().HandleAsync(Ack("HC0000000000001234"), Ct)).ShouldBeNull();

        Logged("hl7.ack").ShouldHaveSingleItem()[2].ShouldBe(true);
    }

    [Fact]
    public async Task Answers_a_qbp_host_query_with_rsp_k11_and_the_orders()
    {
        var rsp = (await Session().HandleAsync(Qbp, Ct)).ShouldNotBeNull();

        var segments = rsp.TrimEnd('\r').Split('\r');
        segments[0].ShouldStartWith(@"MSH|^~\&|HC360|NanaHospitals|cobas|Roche|20260925103016||RSP^K11^RSP_K11|HC");
        segments[1].ShouldBe("MSA|AA|Q00001");
        segments[2].ShouldBe("QAK|TAG01|OK|WOS^Work Order Step^IHE_LABTF");
        segments[3].ShouldBe("QPD|WOS^Work Order Step^IHE_LABTF|TAG01|SMP-1");
        segments[4].ShouldBe("PID|1||NH-UH-1^^^HC360^MR||Ravi Sharma||19800115|M");
        segments.ShouldContain("ORC|NW|SMP-1");
        segments.ShouldContain("OBR|1|SMP-1||GLU^GLU^99HC360");
        segments.ShouldContain("OBR|2|SMP-1||CREA^CREA^99HC360");
        await _processor.Received(1).QueryOrdersAsync(LabSamples.Hl7Instrument, Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "SMP-1" })), Qbp, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Answers_nf_when_nothing_is_ordered_for_the_sample()
    {
        _processor.QueryOrdersAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(HostQueryAnswer.Answer([]));

        var rsp = (await Session().HandleAsync(Qbp, Ct)).ShouldNotBeNull();

        rsp.ShouldContain("\rMSA|AA|Q00001\rQAK|TAG01|NF|");
        rsp.ShouldNotContain("ORC|");
    }

    [Fact]
    public async Task Refuses_host_queries_while_they_are_off_and_asks_again_when_the_lis_cannot_answer()
    {
        _processor.QueryOrdersAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(HostQueryAnswer.Off("off"), HostQueryAnswer.Unavailable("The LIS could not answer the query."));

        var off = (await Session().HandleAsync(Qbp, Ct)).ShouldNotBeNull();
        var unavailable = (await Session().HandleAsync(Qbp, Ct)).ShouldNotBeNull();

        off.ShouldContain("\rMSA|AR|Q00001|Host query is not enabled for this analyser.\rQAK|TAG01|AR|");
        unavailable.ShouldContain("\rMSA|AE|Q00001|The LIS could not answer the query.\rQAK|TAG01|AE|");
    }

    [Fact]
    public async Task Answers_ae_to_a_query_that_names_no_sample()
    {
        var rsp = (await Session().HandleAsync(Qbp.Replace("|TAG01|SMP-1", "|TAG01|", StringComparison.Ordinal), Ct)).ShouldNotBeNull();

        rsp.ShouldContain("QAK|TAG01|AE");
        await _processor.DidNotReceiveWithAnyArgs().QueryOrdersAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Acknowledges_a_qry_q02_and_then_sends_its_orders()
    {
        var pipe = new DuplexPipe();
        var run = Session().RunAsync(pipe.Server, Ct);

        await SendAsync(pipe, Qry);
        var qck = await ReadMessageAsync(pipe);
        var order = await ReadMessageAsync(pipe);
        await SendAsync(pipe, Ack(ControlId(order), trigger: "O01"));
        await Task.Delay(100, Ct);
        await pipe.CloseClientAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        qck.ShouldContain("|QCK^Q02|");
        qck.ShouldContain("\rMSA|AA|Q00002\rQAK|QRY7|OK");
        order.ShouldContain("|ORM^O01|");
        order.ShouldContain("\rORC|NW|SMP-1\r");
        order.ShouldContain("OBR|1|SMP-1||GLU^GLU^99HC360|R");
        Logged("hl7.orders").ShouldHaveSingleItem()[2].ShouldBe(true);
    }

    [Fact]
    public async Task Sends_queued_orders_and_waits_for_their_acknowledgement()
    {
        var download = new LabDownload(Guid.CreateVersion7(), InstrumentOrderAction.Cancel, Order with { ChannelCodes = ["CREA"] });
        IReadOnlyList<LabDownload> once = [download];
        _processor.NextDownloadsAsync(default!, default, Ct).ReturnsForAnyArgs(once, NoDownloads);
        var pipe = new DuplexPipe();
        var run = Session().RunAsync(pipe.Server, Ct);

        var order = await ReadMessageAsync(pipe);

        // Results arriving before the acknowledgement are answered as usual.
        await SendAsync(pipe, LabSamples.HematologyOru);
        (await ReadMessageAsync(pipe)).ShouldEndWith("\rMSA|AA|MSG00042\r");
        await SendAsync(pipe, Ack(ControlId(order)));
        await Task.Delay(100, Ct);
        await pipe.CloseClientAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        order.ShouldContain("|OML^O21^OML_O21|");
        order.ShouldContain("\rORC|CA|SMP-1\r");
        order.ShouldContain("\rSPM|1|SMP-1\r");
        var completed = Completed().ShouldHaveSingleItem();
        completed[2].ShouldBe(true);
        ((IReadOnlyList<LabDownload>)completed[1]!).ShouldBe([download]);
        await _processor.ReceivedWithAnyArgs(1).SubmitResultsAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task An_order_the_analyser_rejects_is_tried_again_later()
    {
        IReadOnlyList<LabDownload> once = [new LabDownload(Guid.CreateVersion7(), InstrumentOrderAction.New, Order)];
        _processor.NextDownloadsAsync(default!, default, Ct).ReturnsForAnyArgs(once, NoDownloads);
        var pipe = new DuplexPipe();
        var run = Session(instrument: LabSamples.Hl7Instrument with { OrderMessage = Hl7OrderMessages.Orm }).RunAsync(pipe.Server, Ct);

        var order = await ReadMessageAsync(pipe);
        await SendAsync(pipe, Ack(ControlId(order), code: "AE") + "|Unknown test CREA");
        await Task.Delay(100, Ct);
        await pipe.CloseClientAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        order.ShouldContain("|ORM^O01|");
        var completed = Completed().ShouldHaveSingleItem();
        completed[2].ShouldBe(false);
        ((string)completed[4]!).ShouldContain("Unknown test CREA");
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
