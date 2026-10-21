using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>Gateway plumbing around the protocol sessions: configuration, allow-lists, status and the LIS bridge.</summary>
public sealed class LabGatewayTests
{
    private static readonly InstrumentResultMessage Sample = new("BC5380", "SMP-26090042",
        [new InstrumentResult("WBC", "7.8", "10*3/uL", null, null)], LabSamples.HematologyOru, LabSamples.ReceivedAt);

    private static readonly InstrumentProfile Bidirectional = new("C311", "Biochemistry", Guid.CreateVersion7(), IsActive: true, IsBidirectional: true, "Astm");

    private readonly Tracing _tracing = new();
    private readonly LabGatewayState _state = new(TimeProvider.System);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private LabMessageProcessor Processor(IInstrumentResultSink? sink)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_tracing.Logger);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IntegrationTrace>();
        if (sink is { } registered)
        {
            services.AddScoped(_ => registered);
        }

        return new LabMessageProcessor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _state, NullLogger<LabMessageProcessor>.Instance);
    }

    private static IInstrumentResultSink Sink(InstrumentProfile? profile = null)
    {
        var sink = Substitute.For<IInstrumentResultSink>();
        sink.GetInstrumentAsync(default!, Ct).ReturnsForAnyArgs(profile ?? Bidirectional);
        return sink;
    }

    [Fact]
    public void Reads_the_instrument_list_as_administrators_wrote_it_before_serial_analysers()
    {
        const string json = """[{"code":"C311","name":"Biochemistry","protocol":"astm","port":5200,"allowedIps":["10.0.5.21"],"hostQuery":false}]""";

        var instrument = JsonSerializer.Deserialize<List<LabInstrumentSettings>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.ShouldHaveSingleItem();

        instrument.Protocol.ShouldBe(LabProtocol.Astm);
        instrument.Transport.ShouldBe(LabTransport.Tcp);
        instrument.Port.ShouldBe(5200);
        instrument.Enabled.ShouldBeTrue();
        instrument.HostQuery.ShouldBe(false);
        instrument.Validate().ShouldBeNull();
    }

    [Fact]
    public void Reads_a_serial_analyser_and_its_port_settings()
    {
        var agent = Guid.CreateVersion7();
        var json = $$$"""[{"code":"XN550","protocol":"Astm","transport":"Serial","agentDeviceId":"{{{agent}}}","serial":{"portName":"com3","baudRate":9600,"parity":"Even","dataBits":7,"stopBits":"Two","handshake":"XOnXOff","rtsEnable":false}}]""";

        var instrument = JsonSerializer.Deserialize<List<LabInstrumentSettings>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.ShouldHaveSingleItem();

        instrument.Transport.ShouldBe(LabTransport.Serial);
        instrument.AgentDeviceId.ShouldBe(agent);
        instrument.HostQuery.ShouldBeNull();
        var serial = instrument.Serial.ShouldNotBeNull();
        serial.PortName.ShouldBe("com3");
        serial.Parity.ShouldBe(LabParity.Even);
        serial.DataBits.ShouldBe(7);
        serial.StopBits.ShouldBe(LabStopBits.Two);
        serial.Handshake.ShouldBe(LabHandshake.XOnXOff);
        serial.DtrEnable.ShouldBeTrue();
        serial.RtsEnable.ShouldBeFalse();
        instrument.Validate().ShouldBeNull();
    }

    [Theory]
    [InlineData("BC 5380", 5100, "instrument code")]
    [InlineData("", 5100, "instrument code")]
    [InlineData("BC5380", 0, "port")]
    [InlineData("BC5380", 70000, "port")]
    public void Validates_instruments(string code, int port, string problem) =>
        new LabInstrumentSettings { Code = code, Port = port }.Validate().ShouldNotBeNull().ShouldContain(problem);

    [Theory]
    [InlineData(false, "COM3", 9600, 8, "lab agent")]
    [InlineData(true, "LPT1", 9600, 8, "COM port")]
    [InlineData(true, "COM0", 9600, 8, "COM port")]
    [InlineData(true, "COM257", 9600, 8, "COM port")]
    [InlineData(true, "COM3", 9601, 8, "baud rate")]
    [InlineData(true, "COM3", 9600, 6, "data bits")]
    public void Validates_serial_analysers(bool withAgent, string port, int baud, int dataBits, string problem) =>
        new LabInstrumentSettings
        {
            Code = "XN550",
            Transport = LabTransport.Serial,
            AgentDeviceId = withAgent ? Guid.CreateVersion7() : null,
            Serial = new LabSerialSettings { PortName = port, BaudRate = baud, DataBits = dataBits },
        }.Validate().ShouldNotBeNull().ShouldContain(problem);

    [Fact]
    public void A_serial_analyser_needs_its_port_settings() =>
        new LabInstrumentSettings { Code = "XN550", Transport = LabTransport.Serial, AgentDeviceId = Guid.CreateVersion7() }
            .Validate().ShouldNotBeNull().ShouldContain("port settings");

    [Fact]
    public void Clamps_runtime_limits_to_safe_bounds()
    {
        var options = LabRuntimeOptions.From(new LabGatewaySettings { MaxMessageKilobytes = 1, IdleTimeoutSeconds = 1 }, LabSamples.Hl7Instrument);

        options.MaxMessageBytes.ShouldBe(16 * 1024);
        options.IdleTimeout.ShouldBe(TimeSpan.FromSeconds(30));
        options.Encoding.ShouldBe(Encoding.Latin1);
        LabGatewaySettings.ResolveEncoding("UTF-8").WebName.ShouldBe("utf-8");
    }

    [Fact]
    public void A_relayed_serial_line_never_idles_out() =>
        LabRuntimeOptions.From(new LabGatewaySettings(), LabSamples.AstmInstrument with { Transport = LabTransport.Serial })
            .IdleTimeout.ShouldBe(Timeout.InfiniteTimeSpan);

    [Fact]
    public void Allow_lists_accept_ipv4_mapped_addresses_and_skip_invalid_entries()
    {
        var allowed = LabInstrumentListener.ParseAllowList(["10.0.5.21", " ::ffff:10.0.5.22 ", "not-an-ip"]).ShouldNotBeNull();

        allowed.ShouldBe([IPAddress.Parse("10.0.5.21"), IPAddress.Parse("10.0.5.22")], ignoreOrder: true);
        LabInstrumentListener.ParseAllowList(null).ShouldBeNull();
        LabInstrumentListener.ParseAllowList(["bogus"]).ShouldBeNull();
    }

    [Fact]
    public void Tracks_listener_status_for_the_dashboard()
    {
        _state.Listening("C311", LabProtocol.Astm, 5200);
        _state.ConnectionOpened("C311");
        _state.RecordMessage("C311", accepted: true, null);
        _state.RecordMessage("C311", accepted: false, "LIS not available");
        _state.Failed("XN", LabProtocol.Hl7v2, 5100, "Port 5100 could not be opened: AddressAlreadyInUse");

        var snapshot = _state.Snapshot();

        snapshot.Select(s => s.InstrumentCode).ShouldBe(["C311", "XN"]);
        snapshot[0].State.ShouldBe("listening");
        snapshot[0].Transport.ShouldBe("tcp");
        snapshot[0].ActiveConnections.ShouldBe(1);
        snapshot[0].MessagesAccepted.ShouldBe(1);
        snapshot[0].MessagesRefused.ShouldBe(1);
        snapshot[0].LastError.ShouldBe("LIS not available");
        snapshot[1].State.ShouldBe("failed");

        _state.ConnectionClosed("C311");
        _state.Stopped("XN");
        _state.Snapshot().ShouldHaveSingleItem().ActiveConnections.ShouldBe(0);
    }

    [Fact]
    public void Tracks_serial_analysers_from_waiting_to_connected_and_port_errors()
    {
        _state.SerialExpected("XN550", LabProtocol.Astm, "COM3");
        _state.Snapshot().ShouldHaveSingleItem().State.ShouldBe("waiting");

        _state.RelayConnected("XN550", LabProtocol.Astm, "Lab PC 1", "COM3");
        var connected = _state.Snapshot().ShouldHaveSingleItem();
        connected.State.ShouldBe("connected");
        connected.Transport.ShouldBe("serial");
        connected.Agent.ShouldBe("Lab PC 1");
        connected.SerialPort.ShouldBe("COM3");
        connected.ConnectedSince.ShouldNotBeNull();

        _state.SerialPortReported("XN550", "Lab PC 1", open: false, "Access to the port 'COM3' is denied.");
        _state.Snapshot().ShouldHaveSingleItem().State.ShouldBe("port_error");
        _state.SerialPortReported("XN550", "Lab PC 1", open: true, null);
        _state.Snapshot().ShouldHaveSingleItem().State.ShouldBe("connected");

        _state.RelayClosed("XN550");
        _state.Snapshot().ShouldHaveSingleItem().State.ShouldBe("waiting");
        _state.SerialExpected("XN550", LabProtocol.Astm, "COM3");
        _state.Snapshot().ShouldHaveSingleItem().State.ShouldBe("waiting");

        _state.RetainSerial([]);
        _state.Snapshot().ShouldBeEmpty();
    }

    /// <summary>
    /// With several servers each reports its part of an analyser's status (SCAL-01), and Lab setup shows one: the relay's or
    /// the agent's over the gateway's, the latest of two that tell as much, with every server's counts.
    /// </summary>
    [Fact]
    public void Several_servers_parts_of_a_status_make_one()
    {
        var at = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
        LabListenerStatus Said(string state, int minute, long accepted = 0, string? error = null) =>
            new("XN550", "Astm", 0, state, state == "connected" ? 1 : 0, accepted, error is null ? 0 : 1, null,
                error is null ? null : at.AddMinutes(minute), error)
            {
                Transport = "serial",
                ChangedAt = at.AddMinutes(minute),
            };

        // The node that runs the jobs expects the analyser; the API node its agent reached relays it, and took 3 messages.
        var merged = LabStatusBoard.Merge([Said("waiting", 5), Said("connected", 1, accepted: 3)]).ShouldHaveSingleItem();
        merged.State.ShouldBe("connected");
        merged.ActiveConnections.ShouldBe(1);
        merged.MessagesAccepted.ShouldBe(3);

        // Its agent's heartbeat, on a third server, says the COM port failed: the latest word.
        merged = LabStatusBoard.Merge([Said("waiting", 5), Said("connected", 1, accepted: 3), Said("port_error", 2, error: "COM3 is denied.")])
            .ShouldHaveSingleItem();
        merged.State.ShouldBe("port_error");
        merged.LastError.ShouldBe("COM3 is denied.");
        merged.MessagesAccepted.ShouldBe(3);

        // A server that only heard the agent's heartbeat, before the gateway set the analyser up, has it waiting.
        var heartbeatOnly = new LabGatewayState(TimeProvider.System);
        heartbeatOnly.SerialPortReported("XN550", "Lab PC 1", open: true, null);
        heartbeatOnly.Snapshot().ShouldHaveSingleItem().State.ShouldBe("waiting");
        LabStatusBoard.Merge([.. heartbeatOnly.Snapshot(), Said("connected", 1)]).ShouldHaveSingleItem().State.ShouldBe("connected");
    }

    [Fact]
    public async Task Refuses_results_when_no_lis_is_registered_so_the_analyser_retransmits()
    {
        var outcome = await Processor(sink: null).SubmitResultsAsync(LabSamples.Hl7Instrument, [Sample], LabSamples.HematologyOru, Ct);

        outcome.Accepted.ShouldBeFalse();
        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Provider.ShouldBe("hl7:BC5380");
        entry.Direction.ShouldBe(IntegrationDirection.Inbound);
        entry.Error!.ShouldContain("No LIS result sink");
        _state.Snapshot().ShouldHaveSingleItem().MessagesRefused.ShouldBe(1);
    }

    [Fact]
    public async Task Hands_results_to_the_lis_and_logs_the_masked_message()
    {
        var sink = Substitute.For<IInstrumentResultSink>();
        sink.ReceiveAsync(default!, Ct).ReturnsForAnyArgs(Result.Success());

        var outcome = await Processor(sink).SubmitResultsAsync(LabSamples.Hl7Instrument, [Sample], LabSamples.HematologyOru, Ct);

        outcome.ShouldBe(LabSubmitOutcome.Ok);
        await sink.Received(1).ReceiveAsync(Sample, Arg.Any<CancellationToken>());
        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Success.ShouldBeTrue();
        entry.Reference.ShouldBe("SMP-26090042");
        entry.Response.ShouldBe("1 result(s) accepted");
        entry.Request!.ShouldContain("SMP-26090042");
        _tracing.AllText.ShouldNotContain("SHARMA");
    }

    [Fact]
    public async Task Reports_samples_the_lis_rejects_or_fails_to_store()
    {
        var sink = Substitute.For<IInstrumentResultSink>();
        sink.ReceiveAsync(Arg.Is<InstrumentResultMessage>(m => m.SampleBarcode == "S-1"), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(Error.Rule("lis.sample_unknown", "Sample S-1 is not registered.")));
        sink.ReceiveAsync(Arg.Is<InstrumentResultMessage>(m => m.SampleBarcode == "S-2"), Arg.Any<CancellationToken>())
            .Returns<Result>(_ => throw new InvalidOperationException("database down"));

        var outcome = await Processor(sink).SubmitResultsAsync(LabSamples.Hl7Instrument,
            [Sample with { SampleBarcode = "S-1" }, Sample with { SampleBarcode = "S-2" }], LabSamples.HematologyOru, Ct);

        outcome.Accepted.ShouldBeFalse();
        outcome.Error.ShouldBe("S-1: Sample S-1 is not registered.; S-2: could not be stored");
    }

    [Fact]
    public async Task Answers_host_queries_from_the_lis_or_refuses_without_one()
    {
        var sink = Sink();
        var order = new InstrumentOrder("SMP-1", "SHARMA^RAVI", "M", null, ["GLU"]);
        sink.GetOrderForSampleAsync("C311", "SMP-1", Arg.Any<CancellationToken>()).Returns(order);

        var answer = await Processor(sink).QueryOrdersAsync(LabSamples.AstmInstrument, ["SMP-1", "SMP-404"], "Q|1|^SMP-1", Ct);
        var refused = await Processor(sink: null).QueryOrdersAsync(LabSamples.AstmInstrument, ["SMP-1"], "Q|1|^SMP-1", Ct);

        answer.Status.ShouldBe(HostQueryStatus.Answered);
        answer.Orders.ShouldHaveSingleItem().ShouldBe(order);
        refused.Status.ShouldBe(HostQueryStatus.Unavailable);
        _tracing.Entries.Select(e => e.Provider).ShouldAllBe(p => p == "astm:C311");
    }

    [Theory]
    [InlineData(false, true, true, "not bidirectional in Lab setup")]
    [InlineData(true, false, true, "not in use")]
    [InlineData(true, true, false, "retired \"hostQuery\": false")]
    public async Task Host_queries_follow_bidirectional_in_lab_setup(bool bidirectional, bool active, bool hostQueryFlag, string reason)
    {
        var sink = Sink(Bidirectional with { IsBidirectional = bidirectional, IsActive = active });
        var instrument = LabSamples.AstmInstrument with { HostQuery = hostQueryFlag ? null : false };

        var answer = await Processor(sink).QueryOrdersAsync(instrument, ["SMP-1"], "Q|1|^SMP-1", Ct);

        answer.Status.ShouldBe(HostQueryStatus.TurnedOff);
        answer.Error.ShouldNotBeNull().ShouldContain(reason);
        await sink.DidNotReceiveWithAnyArgs().GetOrderForSampleAsync(default!, default!, Ct);
        var entry = _tracing.Entries.ShouldHaveSingleItem();
        entry.Success.ShouldBeFalse();
        entry.Error!.ShouldContain(reason);
    }

    [Fact]
    public async Task A_host_query_for_an_analyser_missing_from_lab_setup_is_turned_off()
    {
        var sink = Substitute.For<IInstrumentResultSink>();
        sink.GetInstrumentAsync(default!, Ct).ReturnsForAnyArgs((InstrumentProfile?)null);

        var answer = await Processor(sink).QueryOrdersAsync(LabSamples.AstmInstrument, ["SMP-1"], "Q|1|^SMP-1", Ct);

        answer.Status.ShouldBe(HostQueryStatus.TurnedOff);
        answer.Error.ShouldNotBeNull().ShouldContain("is not set up in Lab setup");
    }

    [Theory]
    [InlineData("Simulator", "simulator", "training")]
    [InlineData("Serial", "serial", "waiting")]
    public async Task An_analyser_moved_off_the_network_shows_its_new_connection_straight_away(string transport, string shownTransport, string shownState)
    {
        var settings = Substitute.For<ISettingsService>();
        using var gateway = Gateway(settings, new PlatformOptions());
        settings.GetAsync<LabGatewaySettings>(LabSettingKeys.Gateway, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LabGatewaySettings { Enabled = true, BindAddress = "127.0.0.1" });
        var network = LabSamples.AstmInstrument with { Port = FreePort() };
        var moved = network with { Transport = Enum.Parse<LabTransport>(transport), AgentDeviceId = Guid.CreateVersion7(), Serial = new LabSerialSettings { PortName = "COM3" } };
        settings.GetAsync<List<LabInstrumentSettings>>(LabSettingKeys.Instruments, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([network], [moved]);

        await gateway.ReconcileAsync(Ct);
        _state.Snapshot().ShouldHaveSingleItem().State.ShouldBe("listening");

        // The network listener stops in the same pass, and its analyser shows its new connection, not nothing.
        await gateway.ReconcileAsync(Ct);
        var status = _state.Snapshot().ShouldHaveSingleItem();
        (status.InstrumentCode, status.Transport, status.State).ShouldBe(("C311", shownTransport, shownState));
    }

    [Fact]
    public async Task An_analyser_on_a_port_of_the_server_itself_is_not_listened_for_and_says_why()
    {
        // The patient portal's port, which an analyser may have been given before the portal had an address of its own:
        // were the gateway to take it first, the server would not start.
        var portal = FreePort();

        var status = await ReconcileAnalyserOnAsync(portal, new PlatformOptions { PortalHttpsPort = portal });

        (status.InstrumentCode, status.State).ShouldBe(("C311", "failed"));
        status.LastError.ShouldBe($"Port {portal} is used by the HC360 server itself, for the patient portal. " +
                                  "Choose another port for this analyser, in its connection here and on the analyser.");
        using var stillFree = new TcpListener(IPAddress.Loopback, portal);
        stillFree.Start();
    }

    [Fact]
    public async Task An_analyser_on_the_api_references_port_is_listened_for_while_the_reference_is_off()
    {
        var port = FreePort();

        var status = await ReconcileAnalyserOnAsync(port, new PlatformOptions { ApiReferencePort = port });

        status.State.ShouldBe("listening");
    }

    private async Task<LabListenerStatus> ReconcileAnalyserOnAsync(int port, PlatformOptions platform)
    {
        var settings = Substitute.For<ISettingsService>();
        using var gateway = Gateway(settings, platform);
        settings.GetAsync<LabGatewaySettings>(LabSettingKeys.Gateway, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new LabGatewaySettings { Enabled = true, BindAddress = "127.0.0.1" });
        settings.GetAsync<List<LabInstrumentSettings>>(LabSettingKeys.Instruments, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([LabSamples.AstmInstrument with { Port = port }]);

        await gateway.ReconcileAsync(Ct);

        return _state.Snapshot().ShouldHaveSingleItem();
    }

    private LabGatewayHostedService Gateway(ISettingsService settings, PlatformOptions platform)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton(Substitute.For<ICurrentUser>());
        services.AddSingleton<ILogger<IntegrationSettingsReader>>(NullLogger<IntegrationSettingsReader>.Instance);
        services.AddScoped<IntegrationSettingsReader>();
        return new LabGatewayHostedService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(platform), Substitute.For<IHostEnvironment>(), Substitute.For<ILabMessageProcessor>(), _state, TimeProvider.System,
            NullLoggerFactory.Instance);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
