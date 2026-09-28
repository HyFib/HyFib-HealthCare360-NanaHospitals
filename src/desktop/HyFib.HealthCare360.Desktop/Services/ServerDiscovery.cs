using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Makaretu.Dns;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>An HC360 Server Node found on the WiFi through mDNS/DNS-SD.</summary>
internal sealed record DiscoveredServer(
    string InstanceName,
    string HostName,
    int Port,
    IReadOnlyList<IPAddress> Addresses,
    string? Version,
    string? FacilityCode,
    string? CaFingerprint)
{
    /// <summary>Addresses to try, most stable first: the machine name survives DHCP changes, IPs are a fallback.</summary>
    public IEnumerable<Uri> CandidateUris()
    {
        yield return new Uri($"https://{HostName}:{Port}/");
        yield return new Uri($"https://{HostName}.local:{Port}/");
        foreach (var address in Addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
        {
            yield return new Uri($"https://{address}:{Port}/");
        }
    }

    public string Display => $"{FacilityCode ?? "HC360"} · {HostName}{(Addresses.Count > 0 ? $" ({Addresses[0]})" : string.Empty)}";
}

/// <summary>
/// Browses <c>_hc360._tcp.local</c> so staff never type IP addresses (SRS DED-02). The Server Node advertises its
/// host name, port, version, facility code and CA fingerprint in TXT records.
/// </summary>
internal static class ServerDiscovery
{
    private const string ServiceType = "_hc360._tcp";

    public static async Task<IReadOnlyList<DiscoveredServer>> BrowseAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var records = new ConcurrentBag<ResourceRecord>();
        using var mdns = new MulticastService();
        using var discovery = new ServiceDiscovery(mdns);

        void Collect(Message message)
        {
            foreach (var record in message.Answers.Concat(message.AdditionalRecords))
            {
                records.Add(record);
            }
        }

        mdns.AnswerReceived += (_, e) => Collect(e.Message);
        discovery.ServiceInstanceDiscovered += (_, e) =>
        {
            Collect(e.Message);
            mdns.SendQuery(e.ServiceInstanceName, type: DnsType.SRV);
            mdns.SendQuery(e.ServiceInstanceName, type: DnsType.TXT);
        };

        try
        {
            mdns.Start();
            discovery.QueryServiceInstances(ServiceType);
            await Task.Delay(duration / 2, cancellationToken);
            discovery.QueryServiceInstances(ServiceType);
            await Task.Delay(duration / 2, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            Log.Warning(ex, "mDNS discovery failed (multicast may be blocked on this network)");
        }
        finally
        {
            mdns.Stop();
        }

        return Assemble(records.ToList());
    }

    internal static IReadOnlyList<DiscoveredServer> Assemble(IReadOnlyList<ResourceRecord> records)
    {
        var addresses = records.OfType<AddressRecord>()
            .GroupBy(r => Normalize(r.Name.ToString()))
            .ToDictionary(g => g.Key, g => g.Select(r => r.Address).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);

        var servers = new List<DiscoveredServer>();
        foreach (var srv in records.OfType<SRVRecord>().GroupBy(r => r.Name.ToString(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var instance = srv.Name.ToString();
            if (!instance.Contains(ServiceType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var txt = records.OfType<TXTRecord>()
                .Where(t => string.Equals(t.Name.ToString(), instance, StringComparison.OrdinalIgnoreCase))
                .SelectMany(t => t.Strings)
                .Select(s => s.Split('=', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.OrdinalIgnoreCase);

            var target = Normalize(srv.Target.ToString());
            var host = txt.TryGetValue("host", out var advertisedHost) && !string.IsNullOrWhiteSpace(advertisedHost)
                ? advertisedHost
                : target.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? target[..^6] : target;

            servers.Add(new DiscoveredServer(
                instance,
                host,
                srv.Port,
                addresses.TryGetValue(target, out var ips) ? ips : [],
                txt.GetValueOrDefault("ver"),
                txt.GetValueOrDefault("fac"),
                txt.TryGetValue("fp", out var fp) && fp.Length == 64 ? fp.ToUpperInvariant() : null));
        }

        return servers;
    }

    private static string Normalize(string name) => name.TrimEnd('.');
}
