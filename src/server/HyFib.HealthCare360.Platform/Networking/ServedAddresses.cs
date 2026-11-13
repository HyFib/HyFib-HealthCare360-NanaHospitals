using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// Which of the Server Node's own addresses its clients reached it on lately: the local address of each request, recorded by
/// <see cref="ServedAddressesMiddleware"/>. On a server with two network cards, often the hospital LAN and an imaging
/// network, it tells the link monitor which cable the workstations use (DED-06): that one lost is critical, the other a
/// warning. HC360 Desktop pings the server every few seconds and the lab agents every minute, so an address in use is seen
/// well within <see cref="Window"/>. The server's own requests (loopback) say nothing about its cables and do not count.
/// </summary>
public sealed class ServedAddresses
{
    /// <summary>How long a request on an address counts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, long> _lastSeen = new(StringComparer.Ordinal);

    /// <summary>A request arrived on <paramref name="local"/>, the server's address its connection came to, at <paramref name="at"/>.</summary>
    public void Saw(IPAddress? local, DateTimeOffset at)
    {
        if (local is null || IPAddress.IsLoopback(local))
        {
            return;
        }

        // A dual-mode listener reports an IPv4 client's connection on ::ffff:a.b.c.d; the network card has a.b.c.d.
        var address = local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local;
        _lastSeen[address.ToString()] = at.UtcTicks;
    }

    /// <summary>Whether a request arrived on any of <paramref name="addresses"/> within <see cref="Window"/> before <paramref name="now"/>.</summary>
    public bool Recently(IEnumerable<string> addresses, DateTimeOffset now) =>
        addresses.Any(address => _lastSeen.TryGetValue(address, out var ticks) && now.UtcTicks - ticks <= Window.Ticks);
}

/// <summary>Records the server's address each request arrived on (<see cref="ServedAddresses"/>).</summary>
public sealed class ServedAddressesMiddleware(RequestDelegate next, ServedAddresses served, TimeProvider time)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        served.Saw(context.Connection.LocalIpAddress, time.GetUtcNow());
        return next(context);
    }
}
