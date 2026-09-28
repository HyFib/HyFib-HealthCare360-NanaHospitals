using System.Net;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Common;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Tests.TestSupport;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>Gateway plumbing around the protocol sessions: configuration, allow-lists, status and the LIS bridge.</summary>
public sealed class LabGatewayTests
{
    private static readonly InstrumentResultMessage Sample = new("BC5380", "SMP-26090042",
        [new InstrumentResult("WBC", "7.8", "10*3/uL", null, null)], LabSamples.HematologyOru, LabSamples.ReceivedAt);

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

    [Fact]
    public void Reads_the_instrument_list_as_administrators_write_it()
    {
        const string json = """[{"code":"C311","name":"Biochemistry","protocol":"astm","port":5200,"allowedIps":["10.0.5.21"],"hostQuery":false}]""";

        var instrument = JsonSerializer.Deserialize<List<LabInstrumentSettings>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.ShouldHaveSingleItem();

        instrument.Protocol.ShouldBe(LabProtocol.Astm);
        instrument.Port.ShouldBe(5200);
        instrument.Enabled.ShouldBeTrue();
        instrument.HostQuery.ShouldBeFalse();
        instrument.Validate().ShouldBeNull();
    }

    [Theory]
    [InlineData("BC 5380", 5100, "instrument code")]
    [InlineData("", 5100, "instrument code")]
    [InlineData("BC5380", 0, "port")]
    [InlineData("BC5380", 70000, "port")]
    public void Validates_instruments(string code, int port, string problem) =>
        new LabInstrumentSettings { Code = code, Port = port }.Validate().ShouldNotBeNull().ShouldContain(problem);

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
        var sink = Substitute.For<IInstrumentResultSink>();
        var order = new InstrumentOrder("SMP-1", "SHARMA^RAVI", "M", null, ["GLU"]);
        sink.GetOrderForSampleAsync("C311", "SMP-1", Arg.Any<CancellationToken>()).Returns(order);

        var orders = await Processor(sink).QueryOrdersAsync(LabSamples.AstmInstrument, ["SMP-1", "SMP-404"], "Q|1|^SMP-1", Ct);
        var refused = await Processor(sink: null).QueryOrdersAsync(LabSamples.AstmInstrument, ["SMP-1"], "Q|1|^SMP-1", Ct);

        orders.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(order);
        refused.ShouldBeNull();
        _tracing.Entries.Select(e => e.Provider).ShouldAllBe(p => p == "astm:C311");
    }
}
