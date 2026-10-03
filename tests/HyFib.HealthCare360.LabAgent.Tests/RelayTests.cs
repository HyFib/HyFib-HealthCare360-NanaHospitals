using System.Net.WebSockets;
using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;
using HyFib.HealthCare360.Platform.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.LabAgent.Tests;

/// <summary>
/// A serial analyser end to end: the test plays the analyser on an in-memory COM port, the agent relays it, and the
/// server's own relay stream and ASTM session answer on the other end of an in-memory WebSocket.
/// </summary>
public sealed class RelayTests
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private static readonly Assignment Xn550 = new("XN550", "Haematology analyser", "Astm", new SerialSettings("COM3", 9600, 8, "None", "One", "None", true, true));

    private static readonly string[] ResultRecords =
    [
        @"H|\^&|||XN-550^1.0|||||||P|1|20260925103000",
        "P|1||UHID00012345||SHARMA^RAVI||19800115|M",
        @"O|1|000000123^01||^^^WBC\^^^HGB|R||||||N||||||||||||||F",
        "R|1|^^^WBC|7.8|10*3/uL||N||F",
        "R|2|^^^HGB|13.1|g/dL||N||F",
        "L|1|N",
    ];

    private static readonly IReadOnlyList<LabDownload> NoDownloads = [];

    private readonly ILabMessageProcessor _processor = Substitute.For<ILabMessageProcessor>();
    private readonly FakeSerial _serial = new();

    public RelayTests()
    {
        _processor.SubmitResultsAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(LabSubmitOutcome.Ok);
        _processor.NextDownloadsAsync(default!, default, Ct).ReturnsForAnyArgs(NoDownloads);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Backoff Quick() => new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

    private InstrumentRelay Agent(IRelayConnector connector) =>
        new(Xn550, _serial, connector, TimeProvider.System, NullLogger.Instance, Quick(), Quick());

    /// <summary>The server end: its relay stream and the unchanged ASTM session, as the relay endpoint runs them.</summary>
    private async Task ServeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var instrument = new LabInstrumentSettings { Code = "XN550", Protocol = LabProtocol.Astm, Transport = LabTransport.Serial };
        var options = new LabRuntimeOptions(Latin1, 64 * 1024, Timeout.InfiniteTimeSpan, TimeZoneInfo.Utc,
            new AstmTimings(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)))
        {
            OrderPollInterval = TimeSpan.FromMilliseconds(50),
        };
        await using var stream = new WebSocketRelayStream(socket);
        try
        {
            await new AstmSession(instrument, options, _processor, TimeProvider.System, NullLogger.Instance).RunAsync(stream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The test ended.
        }

        await stream.CloseAsync(WebSocketCloseStatus.NormalClosure, "done");
    }

    private async Task WriteAsync(params byte[] bytes)
    {
        await _serial.Analyser.WriteAsync(bytes, Ct);
        await _serial.Analyser.FlushAsync(Ct);
    }

    private async Task<byte?> ReadAsync(TimeSpan wait)
    {
        var buffer = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(wait);
        try
        {
            return await _serial.Analyser.ReadAsync(buffer, timeout.Token) == 1 ? buffer[0] : null;
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The analyser bids until the server's ACK comes back (bytes sent before the relay is up are dropped).</summary>
    private async Task OpenTransferAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await WriteAsync(AstmControl.Enq);
            if (await ReadAsync(TimeSpan.FromMilliseconds(200)) == AstmControl.Ack)
            {
                return;
            }
        }

        throw new TimeoutException("The server never acknowledged the analyser's bid through the agent.");
    }

    private async Task SendResultsAsync()
    {
        await OpenTransferAsync();
        foreach (var frame in AstmFrames.BuildMessage(ResultRecords, Latin1))
        {
            await WriteAsync(frame);
            (await ReadAsync(TimeSpan.FromSeconds(10))).ShouldBe(AstmControl.Ack);
        }

        await WriteAsync(AstmControl.Eot);
    }

    [Fact]
    public async Task Relays_an_analysers_results_to_the_server_and_its_acknowledgements_back()
    {
        using var serving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var connector = new FakeConnector((socket, _) => ServeAsync(socket, serving.Token));
        await using var agent = Agent(connector);
        agent.Start();

        await SendResultsAsync();

        await WaitUntilAsync(() => _processor.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ILabMessageProcessor.SubmitResultsAsync)));
        await _processor.Received(1).SubmitResultsAsync(
            Arg.Is<LabInstrumentSettings>(i => i.Code == "XN550"),
            Arg.Is<IReadOnlyList<InstrumentResultMessage>>(m => m.Count == 1 && m[0].SampleBarcode == "000000123" && m[0].Results.Count == 2),
            Arg.Is(string.Join("\r", ResultRecords)),
            Arg.Any<CancellationToken>());
        agent.Report.ShouldBe(new PortReport("XN550", true, null));
        await serving.CancelAsync();
    }

    [Fact]
    public async Task Never_answers_the_analyser_itself_while_the_server_is_out_of_reach()
    {
        var connector = new FakeConnector((_, _) => Task.CompletedTask) { Refuse = true };
        await using var agent = Agent(connector);
        agent.Start();
        await WaitUntilAsync(() => connector.Attempts >= 2);

        await WriteAsync(AstmControl.Enq);

        (await ReadAsync(TimeSpan.FromMilliseconds(800))).ShouldBeNull("only the server may acknowledge, so a bid goes unanswered and the analyser retries later");
        agent.Report.Open.ShouldBeTrue("the COM port stays open while the server is away");
    }

    [Fact]
    public async Task Connects_again_after_the_server_drops_the_relay()
    {
        using var serving = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var connector = new FakeConnector(async (socket, attempt) =>
        {
            if (attempt == 1)
            {
                // The server restarts: the first relay closes at once.
                await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "restarting", Ct);
                return;
            }

            await ServeAsync(socket, serving.Token);
        });
        await using var agent = Agent(connector);
        agent.Start();

        await WaitUntilAsync(() => connector.Attempts >= 2);
        await SendResultsAsync();

        await WaitUntilAsync(() => _processor.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(ILabMessageProcessor.SubmitResultsAsync)));
        _serial.Opened.ShouldBe(1, "the COM port stays open across reconnections");
        await serving.CancelAsync();
    }

    [Fact]
    public async Task Reports_a_port_it_cannot_open_and_keeps_trying()
    {
        _serial.OpenFailure = new UnauthorizedAccessException("Access to the port 'COM3' is denied.");
        var connector = new FakeConnector((_, _) => Task.CompletedTask);
        await using var agent = Agent(connector);
        agent.Start();

        await WaitUntilAsync(() => agent.Report.Error?.Contains("denied", StringComparison.Ordinal) == true);
        agent.Report.Open.ShouldBeFalse();
        connector.Attempts.ShouldBe(0, "no relay without the port");

        _serial.OpenFailure = null;
        await WaitUntilAsync(() => agent.Report.Open);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50, Ct);
        }

        condition().ShouldBeTrue();
    }

    /// <summary>Hands the agent one end of an in-memory WebSocket and gives the other end to <paramref name="serve"/>.</summary>
    private sealed class FakeConnector(Func<WebSocket, int, Task> serve) : IRelayConnector
    {
        private int _attempts;

        public bool Refuse { get; init; }

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<WebSocket> ConnectAsync(string instrumentCode, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (Refuse)
            {
                throw new WebSocketException("The server cannot be reached.");
            }

            var (agent, server) = Wire.Sockets();
            _ = Task.Run(() => serve(server, attempt), CancellationToken.None);
            return Task.FromResult(agent);
        }
    }
}
