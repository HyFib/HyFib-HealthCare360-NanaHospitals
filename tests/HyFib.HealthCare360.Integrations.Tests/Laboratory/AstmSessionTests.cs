using System.Diagnostics;
using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
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

    private static readonly LabDownload Download = new(Guid.CreateVersion7(), InstrumentOrderAction.New, Order with { PatientId = "NH-UH-1", Priority = OrderPriority.Stat });

    private readonly ILabMessageProcessor _processor = Substitute.For<ILabMessageProcessor>();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 25, 5, 0, 0, TimeSpan.Zero));
    private readonly DuplexPipe _pipe = new();

    public AstmSessionTests()
    {
        IReadOnlyList<InstrumentOrder> orders = [Order];
        _processor.SubmitResultsAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(LabSubmitOutcome.Ok);
        _processor.QueryOrdersAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(HostQueryAnswer.Answer(orders));
        _processor.NextDownloadsAsync(default!, default, Ct).ReturnsForAnyArgs(NoDownloads);
    }

    private static IReadOnlyList<LabDownload> NoDownloads { get; } = [];

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

    /// <summary>
    /// Accepts HC360's transfer: answers its ENQ, ACKs every frame (or NAKs/interrupts the first one) and returns the
    /// record texts until EOT.
    /// </summary>
    private async Task<List<string>> ReceiveTransferAsync(bool nakFirstFrame = false, bool interruptFirstFrame = false)
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
            await SendAsync(interruptFirstFrame && texts.Count == 1 ? AstmControl.Eot : AstmControl.Ack);
        }
    }

    /// <summary>Fails if HC360 sends anything within <paramref name="quiet"/>.</summary>
    private async Task ExpectNothingAsync(TimeSpan quiet)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(quiet);
        var buffer = new byte[1];
        await Should.ThrowAsync<OperationCanceledException>(
            async () => (await _pipe.Client.ReadAsync(buffer, wait.Token)).ShouldBe(0, $"HC360 sent 0x{buffer[0]:X2}"));
    }

    private static LabRuntimeOptions WithBackOff(TimeSpan? contention = null, TimeSpan? busy = null)
    {
        var options = LabSamples.Options();
        return options with
        {
            Timings = options.Timings with
            {
                ContentionDelay = contention ?? options.Timings.ContentionDelay,
                BusyRetryDelay = busy ?? options.Timings.BusyRetryDelay,
            },
        };
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

    private List<object?[]> Completed() =>
        _processor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILabMessageProcessor.CompleteDownloadsAsync))
            .Select(call => call.GetArguments())
            .ToList();

    private void QueueOneDownload(LabDownload? download = null)
    {
        IReadOnlyList<LabDownload> once = [download ?? Download];
        _processor.NextDownloadsAsync(default!, default, Ct).ReturnsForAnyArgs(once, NoDownloads);
    }

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
        _processor.QueryOrdersAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(HostQueryAnswer.Unavailable("The LIS could not answer the query."));
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
    public async Task Acknowledges_but_does_not_answer_queries_when_host_query_is_off()
    {
        _processor.QueryOrdersAsync(default!, default!, default!, Ct)
            .ReturnsForAnyArgs(HostQueryAnswer.Off("Host queries and order download are off: C311 is not bidirectional in Lab setup."));
        var run = Start();

        await OpenAsync();
        await SendAcknowledgedAsync(AstmFrames.BuildMessage(LabSamples.AstmQueryRecords, Latin1));
        await SendAsync(AstmControl.Eot);

        // Nothing is bid for: the next thing on the line is the analyser's own transfer.
        await OpenAsync();
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        Logged("astm.orders").ShouldBeEmpty();
    }

    [Fact]
    public async Task Yields_the_line_when_the_analyser_bids_at_the_same_time()
    {
        var run = Start(options: WithBackOff(contention: TimeSpan.FromSeconds(2)));

        await OpenAsync();
        await SendAcknowledgedAsync(AstmFrames.BuildMessage(LabSamples.AstmQueryRecords, Latin1));
        await SendAsync(AstmControl.Eot);
        await ExpectAsync(AstmControl.Enq);

        // Contention: the analyser bids too. The instrument has priority: HC360 does not answer the colliding ENQ...
        var contention = Stopwatch.StartNew();
        await SendAsync(AstmControl.Enq);
        await ExpectNothingAsync(TimeSpan.FromMilliseconds(300));

        // ...answers the analyser's next bid, made during HC360's back-off, and receives its message...
        await OpenAsync();
        await SendAcknowledgedAsync(ResultFrames);
        await SendAsync(AstmControl.Eot);

        // ...and bids again to deliver the query answer only once the back-off is over.
        var records = await ReceiveTransferAsync();
        contention.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9));
        await FinishAsync(run);

        await _processor.ReceivedWithAnyArgs(1).SubmitResultsAsync(default!, default!, default!, Ct);
        records[^1].ShouldBe("L|1|F");
    }

    [Fact]
    public async Task Keeps_listening_while_it_waits_after_the_analyser_is_busy()
    {
        var run = Start(options: WithBackOff(busy: TimeSpan.FromSeconds(2)));

        await OpenAsync();
        await SendAcknowledgedAsync(AstmFrames.BuildMessage(LabSamples.AstmQueryRecords, Latin1));
        await SendAsync(AstmControl.Eot);
        await ExpectAsync(AstmControl.Enq);
        await SendAsync(AstmControl.Nak);

        // The analyser is busy, then has results to send before HC360 may bid again: they are received at once.
        await OpenAsync();
        await SendAcknowledgedAsync(ResultFrames);
        await SendAsync(AstmControl.Eot);
        await _processor.ReceivedWithAnyArgs(1).SubmitResultsAsync(default!, default!, default!, Ct);

        var records = await ReceiveTransferAsync();
        await FinishAsync(run);

        records[^1].ShouldBe("L|1|F");
        Logged("astm.orders").ShouldHaveSingleItem()[2].ShouldBe(true);
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

        // The transfer is abandoned: the retransmission and every later frame are refused too, so no tail of the
        // message is stored without its start.
        await SendAsync(ResultFrames[1]);
        await ExpectAsync(AstmControl.Nak);
        foreach (var frame in ResultFrames.Skip(2))
        {
            await SendAsync(frame);
            await ExpectAsync(AstmControl.Nak);
        }

        await SendAsync(AstmControl.Eot);

        // The next transfer starts afresh.
        await OpenAsync();
        await SendAcknowledgedAsync(AstmFrames.BuildMessage(["H|\\^&", "L|1|N"], Latin1));
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        Logged("astm.rejected").ShouldHaveSingleItem()[5].ShouldBe("Message exceeded the size limit.");
        await _processor.DidNotReceiveWithAnyArgs().SubmitResultsAsync(default!, default!, default!, Ct);
        Logged("astm.message").ShouldHaveSingleItem()[3].ShouldBe("H|\\^&\rL|1|N");
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

    [Fact]
    public async Task Sends_queued_orders_while_the_line_is_idle()
    {
        QueueOneDownload();
        var run = Start();

        var records = await ReceiveTransferAsync();
        await FinishAsync(run);

        records.Count.ShouldBe(4);
        records[1].ShouldBe("P|1|NH-UH-1|||SHARMA&S&RAVI||19800115|M");
        var order = AstmRecords.Parse(string.Join("\r", records))[2];
        order.Field(3).ShouldBe("SMP-1");
        order.Field(6).ShouldBe("S");
        order.Field(12).ShouldBe("N");
        order.Field(26).ShouldBe("O");
        records[3].ShouldBe("L|1|N");
        var completed = Completed().ShouldHaveSingleItem();
        ((IReadOnlyList<LabDownload>)completed[1]!).ShouldBe([Download]);
        completed[2].ShouldBe(true);
    }

    [Fact]
    public async Task The_analyser_wins_when_it_bids_while_an_order_waits()
    {
        QueueOneDownload();
        var run = Start();

        // HC360 bids to send the order; the analyser bids at the same moment, bids again and goes first.
        await ExpectAsync(AstmControl.Enq);
        await SendAsync(AstmControl.Enq);
        await OpenAsync();
        await SendAcknowledgedAsync(ResultFrames);
        await SendAsync(AstmControl.Eot);

        var records = await ReceiveTransferAsync();
        await FinishAsync(run);

        await _processor.ReceivedWithAnyArgs(1).SubmitResultsAsync(default!, default!, default!, Ct);
        records[2].ShouldStartWith("O|1|SMP-1|");
        Completed().ShouldHaveSingleItem()[2].ShouldBe(true);
    }

    [Fact]
    public async Task Keeps_the_claim_on_orders_for_as_long_as_their_send_takes()
    {
        QueueOneDownload();
        var run = Start(options: LabSamples.Options() with { ClaimRenewInterval = TimeSpan.FromMilliseconds(50) });

        // The analyser takes its time to accept the line: the claim must not lapse meanwhile.
        await ExpectAsync(AstmControl.Enq);
        await Task.Delay(TimeSpan.FromMilliseconds(400), Ct);
        await SendAsync(AstmControl.Ack);
        while (await _pipe.ReadByteAsync(Ct) != AstmControl.Eot)
        {
            await _pipe.ReadUntilAsync(b => b.Count >= 2 && b[^2] == AstmControl.Cr && b[^1] == AstmControl.Lf, Ct);
            await SendAsync(AstmControl.Ack);
        }

        await FinishAsync(run);

        var extended = _processor.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(ILabMessageProcessor.ExtendDownloadClaimAsync));
        extended.ShouldBeGreaterThanOrEqualTo(2);
        Completed().ShouldHaveSingleItem()[2].ShouldBe(true);
    }

    [Fact]
    public async Task An_order_the_analyser_refuses_is_tried_again_later()
    {
        QueueOneDownload();
        var run = Start();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await ExpectAsync(AstmControl.Enq);
            await SendAsync(AstmControl.Nak);
        }

        await ExpectAsync(AstmControl.Eot);
        await FinishAsync(run);

        var completed = Completed().ShouldHaveSingleItem();
        completed[2].ShouldBe(false);
        completed[4].ShouldBe("The analyser refused the transfer.");
    }

    [Fact]
    public async Task A_receiver_interrupt_still_delivers_the_whole_order()
    {
        QueueOneDownload();
        var run = Start();

        var records = await ReceiveTransferAsync(interruptFirstFrame: true);
        await FinishAsync(run);

        records.Count.ShouldBe(4);
        Completed().ShouldHaveSingleItem()[2].ShouldBe(true);
    }

    [Fact]
    public async Task Closes_a_network_connection_that_stays_idle()
    {
        var run = Start(options: LabSamples.Options() with { IdleTimeout = TimeSpan.FromMilliseconds(300) });

        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        run.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task Keeps_a_relayed_line_open_however_long_it_is_quiet()
    {
        var run = Start(options: LabSamples.Options() with { IdleTimeout = Timeout.InfiniteTimeSpan });

        await Task.Delay(TimeSpan.FromMilliseconds(400), Ct);
        run.IsCompleted.ShouldBeFalse();
        await OpenAsync();
        await SendAcknowledgedAsync(ResultFrames);
        await SendAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.ReceivedWithAnyArgs(1).SubmitResultsAsync(default!, default!, default!, Ct);
    }
}
