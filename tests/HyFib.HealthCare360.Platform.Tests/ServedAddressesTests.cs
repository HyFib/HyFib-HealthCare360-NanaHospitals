using System.Net;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Which of the Server Node's own addresses its clients reached it on lately, so the link monitor can tell the cable the
/// workstations use from another on a server with two network cards (DED-06).
/// </summary>
public sealed class ServedAddressesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_address_a_request_arrived_on_counts_for_fifteen_minutes()
    {
        var served = new ServedAddresses();

        served.Saw(IPAddress.Parse("192.168.1.10"), Now);

        served.Recently(["192.168.1.10"], Now.AddMinutes(15)).ShouldBeTrue();
        served.Recently(["192.168.1.10"], Now.AddMinutes(16)).ShouldBeFalse();
        served.Recently(["10.20.0.5"], Now).ShouldBeFalse("no request came to the imaging network's address");
    }

    [Fact]
    public void An_IPv4_address_seen_through_a_dual_mode_listener_counts_and_the_server_itself_does_not()
    {
        var served = new ServedAddresses();

        served.Saw(IPAddress.Parse("::ffff:192.168.1.10"), Now);
        served.Saw(IPAddress.Loopback, Now);
        served.Saw(IPAddress.IPv6Loopback, Now);
        served.Saw(null, Now);

        served.Recently(["192.168.1.10"], Now).ShouldBeTrue();
        served.Recently(["127.0.0.1", "::1"], Now).ShouldBeFalse("the server's own probes say nothing about the workstations' cable");
    }

    [Fact]
    public async Task Every_request_says_which_address_it_arrived_on()
    {
        var served = new ServedAddresses();
        var called = false;
        var middleware = new ServedAddressesMiddleware(_ => { called = true; return Task.CompletedTask; }, served, new FixedTime(Now));
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.10");

        await middleware.InvokeAsync(context);

        called.ShouldBeTrue();
        served.Recently(["192.168.1.10"], Now).ShouldBeTrue();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
