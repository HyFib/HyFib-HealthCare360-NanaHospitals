using System.Net;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Discovery;
using Makaretu.Dns;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-02: the Server Node advertises itself over mDNS/DNS-SD as <c>_hc360._tcp.local</c>, with TXT records for its
/// version, facility code and the hospital CA's fingerprint; and HC360 Desktop, browsing for it (DED-03), reads back what
/// it advertises: the same keys, in the same formats. The desktop reads the TXT record with the same code
/// (<see cref="ServerAdvertisement.Read"/>), so the reading is checked here, where the server's records are made.
/// </summary>
public sealed class ServerAdvertisementTests : IDisposable
{
    private const string MachineName = "HC360-SRV";

    private static readonly IPAddress WifiAddress = IPAddress.Parse("192.168.1.20");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_server_advertises_its_service_instance_port_and_txt_records()
    {
        var fingerprint = HospitalCaFingerprint();

        var profile = Advertise(fingerprint);

        profile.QualifiedServiceName.ToString().ShouldBe("_hc360._tcp.local");
        var srv = profile.Resources.OfType<SRVRecord>().ShouldHaveSingleItem();
        srv.Name.ToString().ShouldBe("HC360-HC360-SRV._hc360._tcp.local");
        srv.Name.ToString().ShouldContain(ServerAdvertisement.ServiceType, Case.Insensitive, "HC360 Desktop takes only instances of the service it browses for");
        srv.Port.ShouldBe((ushort)7360, "the staff app's HTTPS port");

        // HC360 Desktop finds the server's addresses by the SRV record's target, and its TXT record by the instance's name.
        var address = profile.Resources.OfType<ARecord>().ShouldHaveSingleItem();
        address.Name.ToString().ShouldBe(srv.Target.ToString());
        address.Address.ShouldBe(WifiAddress);
        var txt = profile.Resources.OfType<TXTRecord>().ShouldHaveSingleItem();
        txt.Name.ToString().ShouldBe(srv.Name.ToString());
        txt.Strings.ShouldBe(["txtvers=1", "ver=1.4.2", "fac=NH", $"fp={fingerprint}", $"host={MachineName}", "path=/"]);
    }

    [Fact]
    public void HC360_Desktop_reads_back_the_version_facility_fingerprint_and_host_the_server_advertises()
    {
        var fingerprint = HospitalCaFingerprint();
        var txt = Advertise(fingerprint).Resources.OfType<TXTRecord>().Single();

        var read = ServerAdvertisement.Read(txt.Strings);

        read.ShouldBe(new AdvertisedProperties("1.4.2", "NH", fingerprint, MachineName));
        fingerprint.ShouldMatch("^[0-9A-F]{64}$", "the server's CA fingerprint, which the desktop pins as it reads it");
    }

    [Fact]
    public void A_server_without_a_CA_of_its_own_advertises_no_fingerprint_for_HC360_Desktop_to_pin()
    {
        // An administrator-supplied certificate: HC360 holds no CA, so the advertisement offers none.
        var txt = Advertise(caFingerprint: null).Resources.OfType<TXTRecord>().Single();

        txt.Strings.ShouldContain("fp=");
        ServerAdvertisement.Read(txt.Strings).CaFingerprint.ShouldBeNull();
    }

    /// <summary>The advertisement of a server on <see cref="MachineName"/>, on the WiFi address, with the default settings.</summary>
    private static ServiceProfile Advertise(string? caFingerprint)
    {
        var options = new PlatformOptions();
        return MdnsAdvertiser.Profile(options.Network, options.HttpsPort, MachineName, "1.4.2+5f3c2a1", "NH", caFingerprint, [WifiAddress]);
    }

    /// <summary>The fingerprint of the hospital CA, as the server makes the CA on first start and advertises it.</summary>
    private string HospitalCaFingerprint()
    {
        var options = new PlatformOptions { DataDirectory = _root };
        using var certificates = new LanCertificateManager(new DataDirectories(options), options, new EphemeralDataProtectionProvider(), NullLogger<LanCertificateManager>.Instance);
        certificates.EnsureCertificates();
        return certificates.CaFingerprint.ShouldNotBeNull();
    }
}
