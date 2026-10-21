using System.Net;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>
/// Several API nodes behind a reverse proxy (SCAL-01): a request the proxy forwards comes from the proxy's address, and
/// the client's own address is in <c>X-Forwarded-For</c>. The server takes it from there only when the request comes from
/// one of the proxies the site names (<see cref="NetworkOptions.KnownProxies"/>, <see cref="NetworkOptions.KnownNetworks"/>),
/// so per-address rate limits (sign-in, one-time codes, the portal), the audit trail, device and webhook address checks
/// and the API reference's local-only check see the real client. Anyone else's header is ignored: a client cannot claim
/// another address by sending one. Off unless proxies are named, and the loopback address is not trusted by default.
/// </summary>
public static class ReverseProxy
{
    /// <summary>The forwarded-header settings for the named proxies, or null when none are named.</summary>
    public static ForwardedHeadersOptions? Options(NetworkOptions network)
    {
        ArgumentNullException.ThrowIfNull(network);
        if (!network.BehindReverseProxy)
        {
            return null;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,

            // Each hop through a named proxy is undone, up to the first address that is not one: that is the client. The
            // host header is never taken from a proxy (X-Forwarded-Host): links and the listener gate keep the real one.
            ForwardLimit = null,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in network.KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy.Trim()));
        }

        foreach (var cidr in network.KnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr.Trim()));
        }

        return options;
    }

    /// <summary>The site's proxy settings that are not addresses or networks, for the start-up check.</summary>
    public static IReadOnlyList<string> Problems(NetworkOptions network)
    {
        ArgumentNullException.ThrowIfNull(network);
        var problems = new List<string>();
        foreach (var proxy in network.KnownProxies.Where(p => !IPAddress.TryParse(p.Trim(), out _)))
        {
            problems.Add($"HealthCare360:Network:KnownProxies lists '{proxy}', which is not an IP address.");
        }

        foreach (var cidr in network.KnownNetworks.Where(n => !System.Net.IPNetwork.TryParse(n.Trim(), out _)))
        {
            problems.Add($"HealthCare360:Network:KnownNetworks lists '{cidr}', which is not a network such as 10.0.0.0/24.");
        }

        return problems;
    }

    /// <summary>
    /// Takes the client's address from a named proxy's forwarded headers. Call first, before anything that looks at the
    /// client's address or the scheme (the listener gate, HTTPS redirection, rate limits, logging).
    /// </summary>
    public static WebApplication UseHospitalForwardedHeaders(this WebApplication app, NetworkOptions network)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (Options(network) is { } options)
        {
            app.UseForwardedHeaders(options);
        }

        return app;
    }
}
