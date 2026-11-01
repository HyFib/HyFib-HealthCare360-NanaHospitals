using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.WindowsInterop.Discovery;
using Makaretu.Dns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// Advertises the Server Node on the hospital WiFi via mDNS/DNS-SD (<c>_hc360._tcp.local</c>, DED-02) so HC360
/// Desktop finds it without anyone typing IP addresses. TXT records (<see cref="ServerAdvertisement"/>): version, facility
/// code, CA fingerprint (and after a rollover the previous CA's), host name and path. Re-announces periodically and whenever the network address changes (WiFi
/// reconnect / DHCP renewal).
/// </summary>
internal sealed class MdnsAdvertiser(
    IOptions<PlatformOptions> options,
    LanCertificateManager certificates,
    IServiceScopeFactory scopeFactory,
    ILogger<MdnsAdvertiser> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Network.EnableMdns || !settings.ServesApi)
        {
            return;
        }

        var facilityCode = await FacilityCodeAsync(stoppingToken) ?? settings.DefaultFacilityCode;
        var version = typeof(MdnsAdvertiser).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

        try
        {
            using var multicast = new MulticastService();
            using var discovery = new ServiceDiscovery(multicast);
            var profile = Profile(settings.Network, settings.HttpsPort, Environment.MachineName, version, facilityCode, certificates.CaFingerprint,
                previousCaFingerprint: certificates.PreviousCaFingerprint);

            multicast.Start();
            discovery.Advertise(profile);
            discovery.Announce(profile, 3);
            logger.LogInformation("Advertising HC360 on the LAN as {Service} port {Port}", settings.Network.MdnsServiceType, settings.HttpsPort);

            var addressChanged = 0;
            void OnAddressChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref addressChanged, 1);
            NetworkChange.NetworkAddressChanged += OnAddressChanged;
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
                var ticks = 0;
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    ticks++;
                    if (Interlocked.Exchange(ref addressChanged, 0) == 1)
                    {
                        // New WiFi address: refresh the TLS certificate SANs and re-announce immediately.
                        if (certificates.RefreshIfNeeded())
                        {
                            logger.LogInformation("Network address changed; LAN certificate re-issued");
                        }

                        discovery.Announce(profile, 2);
                    }
                    else if (ticks % 10 == 0)
                    {
                        discovery.Announce(profile, 1);
                    }
                }
            }
            finally
            {
                NetworkChange.NetworkAddressChanged -= OnAddressChanged;
                discovery.Unadvertise(profile);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "mDNS advertisement is unavailable; workstations must enter the server address manually");
        }
    }

    /// <summary>
    /// What the server advertises: the instance "HC360-" and the machine name, of the configured service type, on the staff
    /// app's HTTPS port, with the TXT record HC360 Desktop reads (<see cref="ServerAdvertisement"/>). Without
    /// <paramref name="addresses"/>, the machine's current addresses.
    /// </summary>
    internal static ServiceProfile Profile(
        NetworkOptions network,
        int httpsPort,
        string machineName,
        string version,
        string facilityCode,
        string? caFingerprint,
        IEnumerable<IPAddress>? addresses = null,
        string? previousCaFingerprint = null)
    {
        var profile = new ServiceProfile(
            new DomainName(ServerAdvertisement.InstanceName(machineName)),
            new DomainName(network.MdnsServiceType),
            (ushort)httpsPort,
            addresses,
            false);
        foreach (var (key, value) in ServerAdvertisement.Properties(version, facilityCode, caFingerprint, machineName, previousCaFingerprint))
        {
            profile.AddProperty(key, value);
        }

        return profile;
    }

    private async Task<string?> FacilityCodeAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            return await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Code).FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Facility code not available for mDNS");
            return null;
        }
    }
}
