using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using HyFib.HealthCare360.WindowsInterop.Discovery;
using Makaretu.Dns;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// An HC360 Server Node found on the WiFi through mDNS/DNS-SD, or listening on this computer
/// (<see cref="OnThisComputer"/>). After its CA was rolled over it announces the CA it replaced too
/// (<see cref="PreviousCaFingerprint"/>), which workstations that have not moved yet pin.
/// </summary>
internal sealed record DiscoveredServer(
    string InstanceName,
    string HostName,
    int Port,
    IReadOnlyList<IPAddress> Addresses,
    string? Version,
    string? FacilityCode,
    string? CaFingerprint,
    bool OnThisComputer = false,
    string? PreviousCaFingerprint = null)
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

    public string Display => OnThisComputer
        ? $"This computer · {HostName} (all-in-one)"
        : $"{FacilityCode ?? "HC360"} · {HostName}{(Addresses.Count > 0 ? $" ({Addresses[0]})" : string.Empty)}";
}

/// <summary>
/// Browses <c>_hc360._tcp.local</c> so staff never type IP addresses (SRS DED-02). The Server Node advertises its
/// host name, port, version, facility code and CA fingerprint in TXT records, which are read as the server writes them
/// (<see cref="ServerAdvertisement"/>).
/// </summary>
internal static class ServerDiscovery
{
    /// <summary>The HC360 server's HTTPS port unless IT changed it.</summary>
    public const int DefaultPort = 7360;

    private const string ServiceType = ServerAdvertisement.ServiceType;

    /// <summary>
    /// The server on this computer, for an all-in-one installation (SRS DED-03), when something listens on
    /// <paramref name="port"/> here. mDNS often misses it: multicast does not always loop back to the computer that
    /// sends it. The machine name is used, as for a server found on the network; its certificate names it.
    /// </summary>
    public static async Task<DiscoveredServer?> ThisComputerAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }

        return new DiscoveredServer(
            "This computer", Environment.MachineName, port, [IPAddress.Loopback], null, null, null,
            OnThisComputer: true);
    }

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

            var txt = ServerAdvertisement.Read(records.OfType<TXTRecord>()
                .Where(t => string.Equals(t.Name.ToString(), instance, StringComparison.OrdinalIgnoreCase))
                .SelectMany(t => t.Strings));

            var target = Normalize(srv.Target.ToString());
            var host = txt.HostName ?? (target.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? target[..^6] : target);

            servers.Add(new DiscoveredServer(
                instance,
                host,
                srv.Port,
                addresses.TryGetValue(target, out var ips) ? ips : [],
                txt.Version,
                txt.FacilityCode,
                txt.CaFingerprint,
                PreviousCaFingerprint: txt.PreviousCaFingerprint));
        }

        return servers;
    }

    private static string Normalize(string name) => name.TrimEnd('.');
}
