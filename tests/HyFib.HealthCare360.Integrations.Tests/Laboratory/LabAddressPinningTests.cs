using System.Net;
using System.Net.Sockets;
using HyFib.HealthCare360.Integrations.Features.LabConnections;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>
/// A network analyser is pinned to its fixed IP address (ASVS V13.2.1): it connects without signing in, so its address is
/// the only thing that says the results are its own. A device at another address is refused before anything it sends is
/// read, and listed (Lab setup, System health); a connection saved with no address is warned of until one is entered.
/// </summary>
public sealed class LabAddressPinningTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_device_that_is_not_at_the_analysers_address_is_refused_unread_and_listed()
    {
        var state = new LabGatewayState(TimeProvider.System);
        var processor = Substitute.For<ILabMessageProcessor>();
        var instrument = new LabInstrumentSettings
        {
            Code = "EM360",
            Protocol = LabProtocol.Astm,
            Transport = LabTransport.Tcp,
            Port = FreePort(),
            AllowedIps = ["10.0.5.21"],
        };
        await using (var listener = new LabInstrumentListener(
                         instrument, IPAddress.Loopback, LabRuntimeOptions.From(new LabGatewaySettings(), instrument), 4, processor, state, TimeProvider.System,
                         NullLogger.Instance, PassThroughGate()))
        {
            listener.Start();

            // Something on the lab network (here, this machine) connects and sends a result: the connection is closed.
            using var device = new TcpClient();
            await device.ConnectAsync(IPAddress.Loopback, instrument.Port, Ct);
            var stream = device.GetStream();
            await stream.WriteAsync("\u0005"u8.ToArray(), Ct);
            var buffer = new byte[16];
            var read = await ReadOrClosedAsync(stream, buffer);
            read.ShouldBe(0, "the gateway closed the connection without answering");

            var refused = await RefusedAsync(state, "EM360");
            refused.Address.ShouldBe("127.0.0.1");
            refused.Count.ShouldBe(1);
            state.Snapshot().Single().LastError.ShouldBe("Refused a connection from 127.0.0.1: not the analyser's IP address.");
        }

        await processor.DidNotReceiveWithAnyArgs().SubmitResultsAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public void Repeated_refusals_are_counted_per_address_and_a_scan_does_not_grow_the_list_without_end()
    {
        var state = new LabGatewayState(TimeProvider.System);
        state.Listening("EM360", LabProtocol.Astm, 5200);
        state.AddressRefused("EM360", "10.0.5.99");
        state.AddressRefused("EM360", "10.0.5.99");
        for (var i = 1; i <= 15; i++)
        {
            state.AddressRefused("EM360", $"10.0.6.{i}");
        }

        var refused = state.Snapshot().Single().RefusedAddresses;
        refused.Count.ShouldBe(LabGatewayState.RefusedAddressesKept);
        refused[0].Address.ShouldBe("10.0.6.15", "most recent first");
        refused.ShouldNotContain(r => r.Address == "10.0.5.99", "pushed out by ten newer addresses");

        state.AddressRefused("EM360", "10.0.6.10");
        state.Snapshot().Single().RefusedAddresses[0].ShouldSatisfyAllConditions(r => r.Address.ShouldBe("10.0.6.10"), r => r.Count.ShouldBe(2));
    }

    [Fact]
    public void What_several_servers_refused_is_shown_as_one_list()
    {
        LabRefusedAddress At(string address, long count, int minute) => new(address, count, Now.AddMinutes(minute - 30), Now.AddMinutes(minute));
        var merged = LabStatusBoard.MergeRefused([At("10.0.5.99", 2, 1), At("10.0.5.98", 1, 5), At("10.0.5.99", 3, 9)]);

        merged.Select(r => (r.Address, r.Count)).ShouldBe([("10.0.5.99", 5L), ("10.0.5.98", 1L)]);
        merged[0].LastAt.ShouldBe(Now.AddMinutes(9));
    }

    [Fact]
    public void System_health_warns_of_a_network_analyser_open_to_any_device_and_of_addresses_refused_in_the_last_day()
    {
        LabInstrumentSettings[] instruments =
        [
            new() { Code = "EM360", Transport = LabTransport.Tcp, Port = 5200, AllowedIps = ["10.0.5.21"] },
            new() { Code = "c311", Transport = LabTransport.Tcp, Port = 5201 },
            new() { Code = "OLD", Transport = LabTransport.Tcp, Port = 5202, AllowedIps = ["not-an-ip"] },
            new() { Code = "OFF", Transport = LabTransport.Tcp, Port = 5203, Enabled = false },
            new() { Code = "XN550", Transport = LabTransport.Serial, AgentDeviceId = Guid.CreateVersion7(), Serial = new LabSerialSettings() },
            new() { Code = "TRAIN", Transport = LabTransport.Simulator },
        ];
        LabListenerStatus[] statuses =
        [
            new("EM360", "Astm", 5200, "listening", 0, 0, 0, null, null, null)
            {
                RefusedAddresses = [new LabRefusedAddress("10.0.5.99", 3, Now.AddHours(-30), Now.AddHours(-2)), new LabRefusedAddress("10.0.5.50", 1, Now.AddDays(-3), Now.AddDays(-2))],
            },
        ];

        var warnings = LabConnectionHealthWarnings.Warnings(instruments, statuses, Now);

        var open = warnings.Single(w => w.Code == "lab.analyser_any_address");
        open.Message.ShouldStartWith("Analyser C311, OLD takes results from any device on the network");
        open.Severity.ShouldBe("warning");
        var refused = warnings.Single(w => w.Code == "lab.analyser_address_refused");
        refused.Message.ShouldContain("Analyser EM360 refused connections from an address that is not its IP: 10.0.5.99 (3 times, last ");
        refused.Message.ShouldNotContain("10.0.5.50", Case.Sensitive, "refused more than a day ago");

        LabConnectionHealthWarnings.OpenToAnyDevice(instruments[4]).ShouldBeFalse("a serial analyser is relayed by an enrolled agent");
        LabConnectionHealthWarnings.OpenToAnyDevice(instruments[5]).ShouldBeFalse("the training analyser has no network port");
        LabConnectionHealthWarnings.Warnings([instruments[0]], [], Now).ShouldBeEmpty();
    }

    private static async Task<LabRefusedAddress> RefusedAsync(LabGatewayState state, string code)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (state.Snapshot().SingleOrDefault(s => s.InstrumentCode == code)?.RefusedAddresses is [var first, ..])
            {
                return first;
            }

            await Task.Delay(20, Ct);
        }

        throw new ShouldAssertException("The refused address was not listed.");
    }

    private static async Task<int> ReadOrClosedAsync(NetworkStream stream, byte[] buffer)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            return await stream.ReadAsync(buffer, timeout.Token);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The device gate for a connection that asks for no device authentication: it admits the stream without looking
    /// anything up. The address pin is checked before the gate, so a device at another address never reaches it.
    /// </summary>
    private static LabDeviceGate PassThroughGate() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), () => null, TimeProvider.System,
            NullLogger<LabDeviceGate>.Instance);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
