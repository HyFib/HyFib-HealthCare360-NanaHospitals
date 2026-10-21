using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>
/// What per-address limits and counts go by (SEC-06): the rate limits per address (<see cref="RateLimitPolicies"/>), their
/// counts for the hospital (<see cref="ClusterRateLimits"/>), and the portal proof of work's failure counts
/// (<see cref="Security.AutomationSuspicion"/>). A client that can send from many addresses cheaply counts once:
/// <list type="bullet">
/// <item>An IPv4 address as it is, and one an IPv6 address carries (IPv4-mapped, 6to4, Teredo, and NAT64's well-known
/// prefix 64:ff9b::/96) as that IPv4 address.</item>
/// <item>Any other IPv6 address by its /64: a home, a phone on a mobile network or a rented server has at least a /64
/// (RFC 6177, 3GPP) and can send each request from another address in it (RFC 8981), which would sidestep every limit
/// and the work the proof of work asks of a failing address. A client that holds more, a /56 or a /48, has a budget
/// for each of its /64s: a coarser key would put the unrelated subscribers a mobile network gives /64s of one larger
/// prefix on one budget, so the per-account limits and the proof of work stay the defence there.</item>
/// <item>But an IPv6 address on the hospital's own network one by one, as IPv4 addresses are: link-local, unique local and
/// loopback addresses, those in a /64 the server has an address in itself, and those in
/// <see cref="NetworkOptions.LocalNetworks"/>. The workstations on one WiFi share its /64, and must not share one
/// sign-in budget.</item>
/// </list>
/// Only the count goes by this: the audit trail, the logs and address allow-lists keep the client's own address.
/// </summary>
public sealed class ClientAddresses
{
    /// <summary>How long the server's own networks are taken as they were read, before they are read again.</summary>
    private static readonly TimeSpan OwnNetworksFor = TimeSpan.FromMinutes(1);

    /// <summary>NAT64's (and SIIT's) well-known prefix, 64:ff9b::/96 (RFC 6052): the IPv4 address is the last 32 bits.</summary>
    private static readonly byte[] Nat64WellKnownPrefix = [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly IPNetwork[] _configured;
    private readonly Func<IEnumerable<IPAddress>> _ownAddresses;
    private readonly TimeProvider _time;
    private volatile LocalSnapshot? _local;

    public ClientAddresses(IOptions<PlatformOptions> options, TimeProvider time)
        : this(Parse(options.Value.Network.LocalNetworks), OwnAddresses, time)
    {
    }

    internal ClientAddresses(IPNetwork[] configured, Func<IEnumerable<IPAddress>> ownAddresses, TimeProvider time)
    {
        _configured = configured;
        _ownAddresses = ownAddresses;
        _time = time;
    }

    /// <summary>The address <paramref name="address"/>'s requests count as, or <c>unknown</c> without one.</summary>
    public string Key(IPAddress? address) => Key(address, Local());

    internal static string Key(IPAddress? address, IReadOnlyCollection<IPNetwork> local)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        if (CarriedIPv4(address) is { } ipv4)
        {
            return ipv4.ToString();
        }

        // Without its zone (fe80::1%3): the server's interface, not the client's.
        var plain = new IPAddress(address.GetAddressBytes());
        return IsLocalOnly(plain) || local.Any(n => n.Contains(plain)) ? plain.ToString() : Slash64(plain).ToString();
    }

    /// <summary>The site's local networks that are not networks, for the start-up check.</summary>
    public static IReadOnlyList<string> Problems(NetworkOptions network)
    {
        ArgumentNullException.ThrowIfNull(network);
        return [.. network.LocalNetworks.Where(n => !IPNetwork.TryParse(n.Trim(), out _))
            .Select(n => $"HealthCare360:Network:LocalNetworks lists '{n}', which is not a network such as 2001:db8:12::/48.")];
    }

    /// <summary>The /64 of each of the server's own internet IPv6 addresses: the hospital networks it is on itself.</summary>
    internal static IEnumerable<IPNetwork> OwnNetworks(IEnumerable<IPAddress> addresses) =>
        addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && CarriedIPv4(a) is null && !IsLocalOnly(a))
            .Select(a => Slash64(new IPAddress(a.GetAddressBytes())))
            .Distinct();

    /// <summary>
    /// The IPv4 address an IPv4-mapped, 6to4 (RFC 3056), Teredo (RFC 4380) or NAT64 (RFC 6052, the well-known prefix)
    /// address stands for. A server on IPv6 alone that IPv4 clients reach through a translator (SIIT, NAT64) sees each at
    /// 64:ff9b::(its IPv4 address), all of them in one /64, which would otherwise give them all one budget.
    /// </summary>
    private static IPAddress? CarriedIPv4(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.IsIPv6Teredo)
        {
            // The client's own IPv4 address is the last 32 bits, inverted.
            return new IPAddress([(byte)~bytes[12], (byte)~bytes[13], (byte)~bytes[14], (byte)~bytes[15]]);
        }

        if (bytes.AsSpan(0, 12).SequenceEqual(Nat64WellKnownPrefix))
        {
            return new IPAddress(bytes.AsSpan(12, 4));
        }

        return bytes[0] == 0x20 && bytes[1] == 0x02 ? new IPAddress(bytes.AsSpan(2, 4)) : null;
    }

    private static bool IsLocalOnly(IPAddress address) =>
        IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;

    private static IPNetwork Slash64(IPAddress address)
    {
        var prefix = address.GetAddressBytes();
        prefix.AsSpan(8).Clear();
        return new IPNetwork(new IPAddress(prefix), 64);
    }

    // One that is not a network is left out here, and stops the server starting (Problems).
    private static IPNetwork[] Parse(string[] networks)
    {
        var parsed = new List<IPNetwork>();
        foreach (var text in networks)
        {
            if (IPNetwork.TryParse(text.Trim(), out var network))
            {
                parsed.Add(network);
            }
        }

        return [.. parsed];
    }

    private IReadOnlyCollection<IPNetwork> Local()
    {
        var now = _time.GetUtcNow();
        var local = _local;
        if (local is null || now >= local.Until)
        {
            local = new LocalSnapshot([.. _configured, .. OwnNetworks(_ownAddresses())], now + OwnNetworksFor);
            _local = local;
        }

        return local.Networks;
    }

    private static List<IPAddress> OwnAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)];
        }
        catch (NetworkInformationException)
        {
            // Until they can be read, a client on the server's own network counts by its /64, as one from outside does.
            return [];
        }
    }

    private sealed record LocalSnapshot(IPNetwork[] Networks, DateTimeOffset Until);
}
