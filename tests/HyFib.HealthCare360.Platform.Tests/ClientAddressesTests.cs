using System.Net;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SEC-06: what per-address limits and proof-of-work counts go by. A client holding an IPv6 /64 counts once however many
/// of its addresses it sends from, an IPv4 address carried in IPv6 counts as that IPv4 address, and the hospital's own
/// network still counts address by address. The expected keys are those Python's <c>ipaddress</c> module gives
/// (<c>ipv4_mapped</c>, <c>teredo</c>, <c>sixtofour</c>, <c>ip_network(f"{a}/64", strict=False)</c>), an implementation
/// of its own.
/// </summary>
public sealed class ClientAddressesTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2002:cb00:7107::1", "203.0.113.7")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", "192.0.2.45")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::1", "2001:db8:1:3::/64")]
    [InlineData("2400:cb00:2049:1::a29f:1804", "2400:cb00:2049:1::/64")]
    public void An_address_counts_as_the_IPv4_address_it_carries_or_its_IPv6_64(string address, string key) =>
        ClientAddresses.Key(IPAddress.Parse(address), []).ShouldBe(key);

    [Theory]
    [InlineData("fe80::1%3", "fe80::1")]
    [InlineData("fd12:3456:789a:1::1", "fd12:3456:789a:1::1")]
    [InlineData("fec0::1", "fec0::1")]
    [InlineData("::1", "::1")]
    public void An_address_only_a_local_network_has_counts_on_its_own(string address, string key) =>
        ClientAddresses.Key(IPAddress.Parse(address), []).ShouldBe(key);

    [Fact]
    public void A_request_without_an_address_counts_as_unknown() =>
        ClientAddresses.Key(null, []).ShouldBe("unknown");

    [Fact]
    public void An_address_on_the_servers_own_network_counts_on_its_own()
    {
        var addresses = new ClientAddresses([], () => [IPAddress.Parse("2001:db8:aa:bb::10"), IPAddress.Parse("fe80::10"), IPAddress.Parse("192.168.1.10")],
            TimeProvider.System);

        // Two workstations on the server's WiFi, and one somewhere else.
        addresses.Key(IPAddress.Parse("2001:db8:aa:bb:1:2:3:4")).ShouldBe("2001:db8:aa:bb:1:2:3:4");
        addresses.Key(IPAddress.Parse("2001:db8:aa:bb:5:6:7:8")).ShouldBe("2001:db8:aa:bb:5:6:7:8");
        addresses.Key(IPAddress.Parse("2001:db8:aa:bc::99")).ShouldBe("2001:db8:aa:bc::/64");
    }

    [Fact]
    public void An_address_in_the_hospitals_local_networks_counts_on_its_own()
    {
        var options = Options.Create(new PlatformOptions { Network = { LocalNetworks = ["2001:db8:12::/48"] } });
        var addresses = new ClientAddresses(options, TimeProvider.System);

        addresses.Key(IPAddress.Parse("2001:db8:12:5::7")).ShouldBe("2001:db8:12:5::7");
        addresses.Key(IPAddress.Parse("2001:db8:13:5::7")).ShouldBe("2001:db8:13:5::/64");
    }

    [Fact]
    public void The_servers_own_networks_are_read_again_a_minute_later()
    {
        var clock = new Clock(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var own = new List<IPAddress> { IPAddress.Parse("2001:db8:aa:bb::10") };
        var addresses = new ClientAddresses([], () => [.. own], clock);
        var workstation = IPAddress.Parse("2001:db8:aa:cc::1");
        addresses.Key(workstation).ShouldBe("2001:db8:aa:cc::/64");

        // The server joins a second network: within the minute the networks it read stand.
        own.Add(IPAddress.Parse("2001:db8:aa:cc::10"));
        clock.Advance(TimeSpan.FromSeconds(59));
        addresses.Key(workstation).ShouldBe("2001:db8:aa:cc::/64");

        clock.Advance(TimeSpan.FromSeconds(1));
        addresses.Key(workstation).ShouldBe("2001:db8:aa:cc::1");
    }

    [Fact]
    public void Only_the_servers_own_internet_IPv6_addresses_make_networks_of_its_own() =>
        ClientAddresses.OwnNetworks([
                IPAddress.Parse("2001:db8:aa:bb::10"), IPAddress.Parse("2001:db8:aa:bb::11"), IPAddress.Parse("192.168.1.10"),
                IPAddress.Parse("fe80::10%4"), IPAddress.Parse("fd00::10"), IPAddress.Parse("::1"), IPAddress.Parse("::ffff:192.168.1.10"),
                IPAddress.Parse("2001:0:4136:e378:8000:63bf:3fff:fdd2"), IPAddress.Parse("2002:c0a8:10a::1"),
            ])
            .Select(n => n.ToString()).ShouldBe(["2001:db8:aa:bb::/64"]);

    [Fact]
    public void Local_networks_that_are_not_networks_are_reported()
    {
        var problems = ClientAddresses.Problems(new NetworkOptions { LocalNetworks = ["2001:db8:12::/48", "2001:db8:13::"] });

        problems.ShouldHaveSingleItem().ShouldContain("'2001:db8:13::'");
        PlatformOptionsValidator.Problems(new PlatformOptions { Network = { LocalNetworks = ["2001:db8:12::/48 "] } })
            .ShouldNotContain(p => p.Contains("LocalNetworks", StringComparison.Ordinal));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
