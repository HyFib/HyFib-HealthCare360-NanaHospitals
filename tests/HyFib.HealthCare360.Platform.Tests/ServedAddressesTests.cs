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
    private readonly Clocks _clocks = new();

    [Fact]
    public void An_address_a_request_arrived_on_counts_for_fifteen_minutes()
    {
        var served = new ServedAddresses(_clocks);

        served.Saw(IPAddress.Parse("192.168.1.10"));

        _clocks.Pass(TimeSpan.FromMinutes(15));
        served.Recently(["192.168.1.10"]).ShouldBeTrue();
        served.Recently(["10.20.0.5"]).ShouldBeFalse("no request came to the imaging network's address");
        _clocks.Pass(TimeSpan.FromMinutes(1));
        served.Recently(["192.168.1.10"]).ShouldBeFalse();
    }

    /// <summary>
    /// The fifteen minutes are measured on a clock nobody sets. A server clock set back an hour, by hand or by a time
    /// server's correction, must not keep an address recent for an hour more, nor one set forward drop it at once.
    /// </summary>
    [Fact]
    public void Setting_the_server_clock_neither_keeps_an_address_recent_nor_drops_it()
    {
        var served = new ServedAddresses(_clocks);

        served.Saw(IPAddress.Parse("192.168.1.10"));
        _clocks.Wall -= TimeSpan.FromHours(1);
        _clocks.Pass(TimeSpan.FromMinutes(16));
        served.Recently(["192.168.1.10"]).ShouldBeFalse("16 minutes passed, whatever the clock says");

        served.Saw(IPAddress.Parse("192.168.1.10"));
        _clocks.Wall += TimeSpan.FromHours(2);
        _clocks.Pass(TimeSpan.FromMinutes(1));
        served.Recently(["192.168.1.10"]).ShouldBeTrue("a minute passed, whatever the clock says");
    }

    /// <summary>
    /// A request recorded while the link monitor reads comes out a moment ahead of the time it read. It counts: with many
    /// workstations one arrives during most reads, and refusing it would make the cable they use "another cable".
    /// </summary>
    [Fact]
    public void A_request_recorded_while_the_monitor_reads_counts()
    {
        var served = new ServedAddresses(_clocks);

        _clocks.Elapsed += TimeSpan.FromMilliseconds(5);
        served.Saw(IPAddress.Parse("192.168.1.10"));
        _clocks.Elapsed -= TimeSpan.FromMilliseconds(5);

        served.Recently(["192.168.1.10"]).ShouldBeTrue();
    }

    [Fact]
    public void An_IPv4_address_seen_through_a_dual_mode_listener_counts_and_the_server_itself_does_not()
    {
        var served = new ServedAddresses(_clocks);

        served.Saw(IPAddress.Parse("::ffff:192.168.1.10"));
        served.Saw(IPAddress.Loopback);
        served.Saw(IPAddress.IPv6Loopback);
        served.Saw(null);

        served.Recently(["192.168.1.10"]).ShouldBeTrue();
        served.Recently(["127.0.0.1", "::1"]).ShouldBeFalse("the server's own probes say nothing about the workstations' cable");
    }

    [Fact]
    public async Task Every_request_says_which_address_it_arrived_on()
    {
        var served = new ServedAddresses(_clocks);
        var called = false;
        var middleware = new ServedAddressesMiddleware(_ => { called = true; return Task.CompletedTask; }, served);
        var context = new DefaultHttpContext();
        context.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.10");

        await middleware.InvokeAsync(context);

        called.ShouldBeTrue();
        served.Recently(["192.168.1.10"]).ShouldBeTrue();
    }

    /// <summary>The server's clock (<see cref="Wall"/>), which can be set, and the monotonic one, which only moves on.</summary>
    private sealed class Clocks : TimeProvider
    {
        public DateTimeOffset Wall { get; set; } = new(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);

        /// <summary>The monotonic clock's reading: how long since it started.</summary>
        public TimeSpan Elapsed { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => Wall;

        public override long GetTimestamp() => Elapsed.Ticks;

        /// <summary>Time passes: both clocks move on.</summary>
        public void Pass(TimeSpan span)
        {
            Wall += span;
            Elapsed += span;
        }
    }
}
