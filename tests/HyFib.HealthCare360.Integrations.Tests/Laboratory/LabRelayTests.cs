using System.Net.WebSockets;
using System.Text;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>
/// A serial analyser relayed by a lab agent: the test plays the agent over a real WebSocket (on an in-memory
/// connection), and the unchanged ASTM session runs on the server end of the relay.
/// </summary>
public sealed class LabRelayTests : IAsyncDisposable
{
    private static readonly IReadOnlyList<LabDownload> NoDownloads = [];

    private readonly ILabMessageProcessor _processor = Substitute.For<ILabMessageProcessor>();
    private readonly DuplexPipe _wire = new();
    private readonly WebSocket _server;
    private readonly WebSocket _agent;

    public LabRelayTests()
    {
        _processor.SubmitResultsAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(LabSubmitOutcome.Ok);
        _processor.NextDownloadsAsync(default!, default, Ct).ReturnsForAnyArgs(NoDownloads);
        _server = WebSocket.CreateFromStream(_wire.Server, new WebSocketCreationOptions { IsServer = true, SubProtocol = LabAgentProtocol.Subprotocol });
        _agent = WebSocket.CreateFromStream(_wire.Client, new WebSocketCreationOptions { IsServer = false, SubProtocol = LabAgentProtocol.Subprotocol });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<byte[]> ResultFrames => AstmFrames.BuildMessage(LabSamples.AstmResultRecords, Encoding.Latin1);

    public async ValueTask DisposeAsync()
    {
        _server.Dispose();
        _agent.Dispose();
        await Task.CompletedTask;
    }

    private Task Relay(WebSocketRelayStream stream, LabRuntimeOptions? options = null) =>
        new AstmSession(LabSamples.AstmInstrument with { Transport = LabTransport.Serial }, options ?? LabSamples.Options() with { IdleTimeout = Timeout.InfiniteTimeSpan },
            _processor, TimeProvider.System, NullLogger.Instance).RunAsync(stream, Ct);

    /// <summary>The agent forwards bytes from the analyser's COM port as they come, in pieces of any size.</summary>
    private Task FromAnalyserAsync(params byte[] bytes) => _agent.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, Ct);

    private async Task<byte> ToAnalyserAsync()
    {
        var buffer = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var received = await _agent.ReceiveAsync(buffer, timeout.Token);
        received.MessageType.ShouldBe(WebSocketMessageType.Binary);
        received.Count.ShouldBe(1);
        return buffer[0];
    }

    private async Task FinishAsync(Task run)
    {
        await _agent.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "agent stopping", Ct);
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Relays_an_analysers_transfer_to_the_unchanged_session()
    {
        await using var stream = new WebSocketRelayStream(_server, Ct);
        var run = Relay(stream);

        await FromAnalyserAsync(AstmControl.Enq);
        (await ToAnalyserAsync()).ShouldBe(AstmControl.Ack);
        foreach (var frame in ResultFrames)
        {
            // A COM port hands over a frame in pieces; the relay keeps them in order.
            await FromAnalyserAsync(frame[..3]);
            await FromAnalyserAsync(frame[3..]);
            (await ToAnalyserAsync()).ShouldBe(AstmControl.Ack);
        }

        await FromAnalyserAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.Received(1).SubmitResultsAsync(
            Arg.Any<LabInstrumentSettings>(),
            Arg.Is<IReadOnlyList<InstrumentResultMessage>>(m => m.Count == 1 && m[0].SampleBarcode == "SMP-26090042"),
            Arg.Is(string.Join("\r", LabSamples.AstmResultRecords)),
            Arg.Any<CancellationToken>());
        var (received, sent) = stream.Traffic;
        received.ShouldBe(ResultFrames.Sum(f => f.Length) + 2);
        sent.ShouldBe(ResultFrames.Count + 1);
    }

    [Fact]
    public async Task A_protocol_timeout_never_closes_the_relay()
    {
        var options = LabSamples.Options() with
        {
            IdleTimeout = Timeout.InfiniteTimeSpan,
            Timings = new AstmTimings(TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)),
        };
        await using var stream = new WebSocketRelayStream(_server, Ct);
        var run = Relay(stream, options);

        await FromAnalyserAsync(AstmControl.Enq);
        (await ToAnalyserAsync()).ShouldBe(AstmControl.Ack);
        await FromAnalyserAsync(ResultFrames[0]);
        (await ToAnalyserAsync()).ShouldBe(AstmControl.Ack);

        // The analyser goes quiet past the frame timeout; a cancelled WebSocket receive would have aborted the socket.
        await Task.Delay(TimeSpan.FromMilliseconds(600), Ct);
        _server.State.ShouldBe(WebSocketState.Open);
        await FromAnalyserAsync(AstmControl.Enq);
        (await ToAnalyserAsync()).ShouldBe(AstmControl.Ack);
        foreach (var frame in ResultFrames)
        {
            await FromAnalyserAsync(frame);
            (await ToAnalyserAsync()).ShouldBe(AstmControl.Ack);
        }

        await FromAnalyserAsync(AstmControl.Eot);
        await FinishAsync(run);

        await _processor.ReceivedWithAnyArgs(1).SubmitResultsAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Text_on_the_relay_ends_it_as_a_protocol_error()
    {
        await using var stream = new WebSocketRelayStream(_server, Ct);
        var run = Relay(stream);

        await _agent.SendAsync(Encoding.UTF8.GetBytes("ACK"), WebSocketMessageType.Text, endOfMessage: true, Ct);
        await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        stream.ProtocolError.ShouldNotBeNull().ShouldContain("only the analyser's bytes");
        await _processor.DidNotReceiveWithAnyArgs().SubmitResultsAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task A_newer_relay_for_the_same_analyser_replaces_the_old_one()
    {
        var state = new LabGatewayState(TimeProvider.System);
        var registry = new LabRelayRegistry(state, TimeProvider.System);
        var agent = Guid.CreateVersion7();
        var instrument = SerialInstrument(agent);

        using var first = registry.Open(instrument, agent, "Lab PC 1");
        using var second = registry.Open(instrument, agent, "Lab PC 1");

        first.Ended.IsCancellationRequested.ShouldBeTrue();
        second.Ended.IsCancellationRequested.ShouldBeFalse();
        first.Dispose();
        registry.IsLive("XN550").ShouldBeTrue();
        state.Snapshot().ShouldHaveSingleItem().State.ShouldBe("connected");
        registry.ConnectedAgents().ShouldBe([agent]);

        second.Dispose();
        registry.IsLive("XN550").ShouldBeFalse();
        state.Snapshot().ShouldHaveSingleItem().State.ShouldBe("waiting");
    }

    [Fact]
    public void Ends_the_relays_an_agent_may_no_longer_run()
    {
        var registry = new LabRelayRegistry(new LabGatewayState(TimeProvider.System), TimeProvider.System);
        var agent = Guid.CreateVersion7();
        using var kept = registry.Open(SerialInstrument(agent) with { Code = "XN550" }, agent, "Lab PC 1");
        using var moved = registry.Open(SerialInstrument(agent) with { Code = "EM360" }, agent, "Lab PC 1");

        registry.EndForAgent(agent, code => code == "XN550");

        kept.Ended.IsCancellationRequested.ShouldBeFalse();
        moved.Ended.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task An_agent_gets_only_its_enabled_valid_serial_analysers()
    {
        var agent = Guid.CreateVersion7();
        List<LabInstrumentSettings> stored =
        [
            SerialInstrument(agent),
            SerialInstrument(agent) with { Code = "OFF1", Enabled = false },
            SerialInstrument(Guid.CreateVersion7()) with { Code = "OTHER" },
            SerialInstrument(agent) with { Code = "BAD", Serial = new LabSerialSettings { PortName = "LPT1" } },
            new() { Code = "C311", Port = 5200 },
        ];
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<List<LabInstrumentSettings>>(LabSettingKeys.Instruments, null, Ct).ReturnsForAnyArgs(stored);
        var reader = new IntegrationSettingsReader(settings, Substitute.For<ICurrentUser>(), NullLogger<IntegrationSettingsReader>.Instance);

        var assigned = await LabAgentEndpoints.AssignedAsync(reader, agent, Ct);

        var only = assigned.ShouldHaveSingleItem();
        only.Code.ShouldBe("XN550");
        var assignment = LabAgentEndpoints.ToAssignment(only);
        assignment.Protocol.ShouldBe("Astm");
        assignment.Port.ShouldBe(new LabAgentSerialPort("COM3", 9600, 8, "None", "One", "None", true, true));
    }

    [Theory]
    [InlineData(70, "XN550", null, "at most 64")]
    [InlineData(1, "XN 550", null, "analyser code")]
    [InlineData(1, "XN550", "bad\u0007text", "not allowed")]
    public void Validates_what_an_agent_reports(int ports, string code, string? error, string message)
    {
        var request = new LabAgentHeartbeatRequest("1.0.0", Enumerable.Range(0, ports).Select(_ => new LabAgentPortReport(code, false, error)).ToList());

        new LabAgentHeartbeatRequestValidator().Validate(request).Errors.ShouldContain(e => e.ErrorMessage.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Accepts_a_normal_report() =>
        new LabAgentHeartbeatRequestValidator().Validate(new LabAgentHeartbeatRequest("1.0.0", [new LabAgentPortReport("XN550", false, "Access to the port 'COM3' is denied.")]))
            .IsValid.ShouldBeTrue();

    private static LabInstrumentSettings SerialInstrument(Guid agent) => new()
    {
        Code = "XN550",
        Name = "Haematology analyser",
        Protocol = LabProtocol.Astm,
        Transport = LabTransport.Serial,
        AgentDeviceId = agent,
        Serial = new LabSerialSettings { PortName = "com3" },
    };
}
