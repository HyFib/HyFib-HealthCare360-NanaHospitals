using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>
/// Drives <see cref="AstmSession"/> over an in-memory connection exactly as an analyser would: the test plays the
/// instrument side of E1381 (ENQ/ACK/NAK/EOT and framed records) byte by byte.
/// </summary>
public sealed class AstmSessionTests
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static readonly InstrumentOrder Order = new("SMP-1", "SHARMA^RAVI", "M", new DateOnly(1980, 1, 15), ["GLU", "CREA"]);

    private readonly ILabMessageProcessor _processor = Substitute.For<ILabMessageProcessor>();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 25, 5, 0, 0, TimeSpan.Zero));
    private readonly DuplexPipe _pipe = new();

    public AstmSessionTests()
    {
        IReadOnlyList<InstrumentOrder> orders = [Order];
        _processor.SubmitResultsAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(LabSubmitOutcome.Ok);
        _processor.QueryOrdersAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(orders);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<byte[]> ResultFrames => AstmFrames.BuildMessage(LabSamples.AstmResultRecords, Latin1);

    private Task Start(LabInstrumentSettings? instrument = null, LabRuntimeOptions? options = null) =>
        new AstmSession(instrument ?? LabSamples.AstmInstrument, options ?? LabSamples.Options(), _processor, _clock, NullLogger.Instance)
            .RunAsync(_pipe.Server, Ct);

    private async Task SendAsync(params byte[] bytes)
    {
        await _pipe.Client.WriteAsync(bytes, Ct);
        await _pipe.Client.FlushAsync(Ct);
    }

    private async Task ExpectAsync(byte expected) => (await _pipe.ReadByteAsync(Ct)).ShouldBe(expected);

    private async Task OpenAsync()
    {
        await SendAsync(AstmControl.Enq);
        await ExpectAsync(AstmControl.Ack);
    }

    private async Task SendAcknowledgedAsync(IEnumerable<byte[]> frames)
    {
        foreach (var frame in frames)
        {
            await SendAsync(frame);
            await ExpectAsync(AstmControl.Ack);
        }
    }

    /// <summary>Accepts HC360's transfer: answers its ENQ, ACKs every frame and returns the record texts until EOT.</summary>
    private async Task<List<string>> ReceiveTransferAsync(bool nakFirstFrame = false)
    {
        await ExpectAsync(AstmControl.Enq);
        await SendAsync(AstmControl.Ack);
        var texts = new List<string>();
        byte[]? previous = null;
        while (true)
        {
            var first = await _pipe.ReadByteAsync(Ct);
            if (first == AstmControl.Eot)
            {
                return texts;
            }

            first.ShouldBe(AstmControl.Stx);
            byte[] frame = [first, .. await _pipe.ReadUntilAsync(b => b.Count >= 2 && b[^2] == AstmControl.Cr && b[^1] == AstmControl.Lf, Ct)];
            AstmFrames.TryParse(frame, Latin1, out var parsed, out var error).ShouldBeTrue(error);
            if (nakFirstFrame && previous is null && texts.Count == 0)
            {
                previous = frame;
                await SendAsync(AstmControl.Nak);
                continue;
            }

            if (previous is not null && texts.Count == 0)
            {
                frame.ShouldBe(previous, "a NAKed frame must be retransmitted unchanged");
            }

            texts.Add(parsed.Text.TrimEnd('\r'));
            await SendAsync(AstmControl.Ack);
        }
    }

    private async Task FinishAsync(Task run)
    {
        await _pipe.CloseClientAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    private List<object?[]> Logged(string operation) =>
        _processor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILabMessageProcessor.LogAsync) && (string?)call.GetArguments()[1] == operation)
            .Select(call => call.GetArguments())
            .ToList();

    [Fact]
    public async Task Acknowledges_every_frame_and_stores_the_results()
    {
        var run = Start();

        await SendAsync(0x00, (byte)'x', AstmControl.Cr); // line noise before the transfer is ignored
        await OpenAsync();
        await SendAcknowledgedAsync(ResultFrames);
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.Received(1).SubmitResultsAsync(
            Arg.Is(LabSamples.AstmInstrument),
            Arg.Is<IReadOnlyList<InstrumentResultMessage>>(m => m.Count == 1 && m[0].SampleBarcode == "SMP-26090042" && m[0].Results.Count == 2),
            Arg.Is(string.Join("\r", LabSamples.AstmResultRecords)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Naks_the_final_frame_until_the_lis_has_stored_the_results()
    {
        _processor.SubmitResultsAsync(default!, default!, default!, Ct)
            .ReturnsForAnyArgs(new LabSubmitOutcome(false, "LIS not available"), LabSubmitOutcome.Ok);
        var run = Start();

        await OpenAsync();
        await SendAcknowledgedAsync(ResultFrames.SkipLast(1));
        await SendAsync(ResultFrames[^1]);
        await ExpectAsync(AstmControl.Nak);
        await SendAsync(ResultFrames[^1]);
        await ExpectAsync(AstmControl.Ack);
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.ReceivedWithAnyArgs(2).SubmitResultsAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Naks_corrupted_frames_and_accepts_the_retransmission()
    {
        var run = Start();
        var corrupted = ResultFrames[0].ToArray();
        corrupted[3] = (byte)'X';

        await OpenAsync();
        await SendAsync(corrupted);
        await ExpectAsync(AstmControl.Nak);
        await SendAcknowledgedAsync(ResultFrames);
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.ReceivedWithAnyArgs(1).SubmitResultsAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task A_repeated_frame_after_a_lost_ack_is_acknowledged_but_applied_once()
    {
        var run = Start();

        await OpenAsync();
        for (var i = 0; i < ResultFrames.Count; i++)
        {
            await SendAsync(ResultFrames[i]);
            await ExpectAsync(AstmControl.Ack);
            if (i == 3)
            {
                // The analyser did not see our ACK for the GLU result and sends the same frame again.
                await SendAsync(ResultFrames[i]);
                await ExpectAsync(AstmControl.Ack);
            }
        }

        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.Received(1).SubmitResultsAsync(
            Arg.Any<LabInstrumentSettings>(),
            Arg.Is<IReadOnlyList<InstrumentResultMessage>>(m => m[0].Results.Count == 2),
            Arg.Is(string.Join("\r", LabSamples.AstmResultRecords)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Naks_frames_out_of_sequence()
    {
        var run = Start();

        await OpenAsync();
        await SendAsync(AstmFrames.Build(2, "H|\\^&\r", true, Latin1));
        await ExpectAsync(AstmControl.Nak);
        await SendAsync(AstmFrames.Build(1, "H|\\^&\r", true, Latin1));
        await ExpectAsync(AstmControl.Ack);
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.DidNotReceiveWithAnyArgs().SubmitResultsAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Answers_a_host_query_after_the_analyser_releases_the_line()
    {
        var run = Start();

        await OpenAsync();
        await SendAcknowledgedAsync(AstmFrames.BuildMessage(LabSamples.AstmQueryRecords, Latin1));
        await SendAsync(AstmControl.Eot);
        var records = await ReceiveTransferAsync();
        await FinishAsync(run);

        await _processor.Received(1).QueryOrdersAsync(
            Arg.Is(LabSamples.AstmInstrument), Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "SMP-1", "SMP-2" })), Arg.Any<string>(), Arg.Any<CancellationToken>());
        records.Count.ShouldBe(4);
        records[0].ShouldBe(@"H|\^&|||HC360^1.0|||||||P|1|20260925103000");
        records[1].ShouldBe("P|1||||SHARMA&S&RAVI||19800115|M");
        records[2].ShouldStartWith(@"O|1|SMP-1||^^^GLU\^^^CREA|R|");
        records[3].ShouldBe("L|1|F");
        Logged("astm.orders").ShouldHaveSingleItem()[2].ShouldBe(true);
    }

    [Fact]
    public async Task Retransmits_a_frame_the_analyser_naks()
    {
        var run = Start();

        await OpenAsync();
        await SendAcknowledgedAsync(AstmFrames.BuildMessage(LabSamples.AstmQueryRecords, Latin1));
        await SendAsync(AstmControl.Eot);
        var records = await ReceiveTransferAsync(nakFirstFrame: true);
        await FinishAsync(run);

        records.Count.ShouldBe(4);
        records[0].ShouldStartWith("H|");
    }

    [Fact]
    public async Task Naks_a_host_query_the_lis_cannot_answer()
    {
        _processor.QueryOrdersAsync(default!, default!, default!, Ct).ReturnsForAnyArgs((IReadOnlyList<InstrumentOrder>?)null);
        var run = Start();
        var frames = AstmFrames.BuildMessage(LabSamples.AstmQueryRecords, Latin1);

        await OpenAsync();
        await SendAcknowledgedAsync(frames.SkipLast(1));
        await SendAsync(frames[^1]);
        await ExpectAsync(AstmControl.Nak);
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        Logged("astm.orders").ShouldBeEmpty();
    }

    [Fact]
    public async Task Does_not_answer_queries_when_host_query_is_disabled()
    {
        var run = Start(LabSamples.AstmInstrument with { HostQuery = false });

        await OpenAsync();
        await SendAcknowledgedAsync(AstmFrames.BuildMessage(LabSamples.AstmQueryRecords, Latin1));
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.DidNotReceiveWithAnyArgs().QueryOrdersAsync(default!, default!, default!, Ct);
        Logged("astm.query").ShouldHaveSingleItem()[5].ShouldBe("Host query is disabled for this instrument.");
    }

    [Fact]
    public async Task Yields_the_line_when_the_analyser_bids_at_the_same_time()
    {
        var run = Start();

        await OpenAsync();
        await SendAcknowledgedAsync(AstmFrames.BuildMessage(LabSamples.AstmQueryRecords, Latin1));
        await SendAsync(AstmControl.Eot);
        await ExpectAsync(AstmControl.Enq);

        // Contention: the analyser bids too. The instrument has priority, so HC360 receives first...
        await SendAsync(AstmControl.Enq);
        await ExpectAsync(AstmControl.Ack);
        await SendAcknowledgedAsync(ResultFrames);
        await SendAsync(AstmControl.Eot);

        // ...and then bids again to deliver the query answer.
        var records = await ReceiveTransferAsync();
        await FinishAsync(run);

        await _processor.ReceivedWithAnyArgs(1).SubmitResultsAsync(default!, default!, default!, Ct);
        records[^1].ShouldBe("L|1|F");
    }

    [Fact]
    public async Task Gives_up_the_answer_when_the_analyser_keeps_refusing_the_line()
    {
        var run = Start();

        await OpenAsync();
        await SendAcknowledgedAsync(AstmFrames.BuildMessage(LabSamples.AstmQueryRecords, Latin1));
        await SendAsync(AstmControl.Eot);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await ExpectAsync(AstmControl.Enq);
            await SendAsync(AstmControl.Nak);
        }

        await ExpectAsync(AstmControl.Eot);
        await FinishAsync(run);

        var logged = Logged("astm.orders").ShouldHaveSingleItem();
        logged[2].ShouldBe(false);
        logged[5].ShouldBe("The analyser refused the transfer.");
    }

    [Fact]
    public async Task Discards_a_message_that_exceeds_the_size_limit()
    {
        var run = Start(options: LabSamples.Options(maxMessageBytes: 100));

        await OpenAsync();
        await SendAsync(ResultFrames[0]);
        await ExpectAsync(AstmControl.Ack);
        await SendAsync(ResultFrames[1]);
        await ExpectAsync(AstmControl.Nak);
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        Logged("astm.rejected").ShouldHaveSingleItem()[5].ShouldBe("Message exceeded the size limit.");
        await _processor.DidNotReceiveWithAnyArgs().SubmitResultsAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Discards_an_incomplete_message_when_the_analyser_goes_quiet()
    {
        var options = LabSamples.Options() with
        {
            Timings = new AstmTimings(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)),
        };
        var run = Start(options: options);

        await OpenAsync();
        await SendAsync(ResultFrames[0]);
        await ExpectAsync(AstmControl.Ack);

        // Silence past the frame timeout: the session drops the partial message and waits for a new ENQ.
        await Task.Delay(TimeSpan.FromMilliseconds(600), Ct);
        await OpenAsync();
        await SendAcknowledgedAsync(ResultFrames);
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.Received(1).SubmitResultsAsync(
            Arg.Any<LabInstrumentSettings>(), Arg.Any<IReadOnlyList<InstrumentResultMessage>>(),
            Arg.Is(string.Join("\r", LabSamples.AstmResultRecords)), Arg.Any<CancellationToken>());
    }
}
