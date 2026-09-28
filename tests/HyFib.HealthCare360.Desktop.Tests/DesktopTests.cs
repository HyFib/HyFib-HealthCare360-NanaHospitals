using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Desktop;
using HyFib.HealthCare360.Desktop.Services;
using Makaretu.Dns;

namespace HyFib.HealthCare360.Desktop.Tests;

public sealed class DeepLinkTests
{
    [Theory]
    [InlineData("hc360://patients/0199a1b2-0000-7000-8000-000000000001", "/patients/0199a1b2-0000-7000-8000-000000000001")]
    [InlineData("hc360://open?path=%2Ffront-office%2Fqueue", "/front-office/queue")]
    [InlineData("HC360://front-office/visits?status=Open", "/front-office/visits?status=Open")]
    public void Resolves_app_paths(string link, string expected) => DeepLinks.Resolve([link]).ShouldBe(expected);

    [Theory]
    [InlineData("hc360://open?path=https%3A%2F%2Fevil.example")]
    [InlineData("hc360://open?path=%2F%2Fevil.example")]
    [InlineData("hc360://open?path=%2F%5Cevil.example")]
    [InlineData("https://evil.example/patients")]
    [InlineData("--some-flag")]
    public void Refuses_anything_that_could_leave_the_app(string link) => DeepLinks.Resolve([link]).ShouldBeNull();

    [Theory]
    [InlineData("/", true)]
    [InlineData("/admin/system", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/x/https://evil.example", false)]
    public void Validates_start_paths(string path, bool safe) => DeepLinks.IsSafePath(path).ShouldBe(safe);
}

public sealed class ServerAddressTests
{
    [Theory]
    [InlineData("HC360-SRV", "https://hc360-srv:7360/")]
    [InlineData("HC360-SRV:8443", "https://hc360-srv:8443/")]
    [InlineData("192.168.1.20", "https://192.168.1.20:7360/")]
    [InlineData("https://hc360.nana.local:7360/some/path", "https://hc360.nana.local:7360/")]
    public void Parses_what_staff_type(string typed, string expected) => SetupWindow.ParseAddress(typed)!.AbsoluteUri.ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://hc360-srv")]
    [InlineData("ftp://hc360-srv")]
    public void Rejects_insecure_or_empty_addresses(string typed) => SetupWindow.ParseAddress(typed).ShouldBeNull();
}

public sealed class ServerTrustTests
{
    [Fact]
    public void Trusts_a_server_certificate_issued_by_the_pinned_ca_for_the_right_host()
    {
        using var ca = CreateCa("CN=Nana Hospitals HC360 CA");
        using var server = CreateServerCertificate(ca, "HC360-SRV", IPAddress.Parse("192.168.1.20"));
        using var trust = new ServerTrust(ca.ExportCertificatePem());

        trust.IsTrusted(server, "HC360-SRV").ShouldBeTrue();
        trust.IsTrusted(server, "192.168.1.20").ShouldBeTrue();
        trust.Fingerprint.ShouldBe(Convert.ToHexString(SHA256.HashData(ca.RawData)));
    }

    [Fact]
    public void Rejects_a_certificate_for_another_host()
    {
        using var ca = CreateCa("CN=Nana Hospitals HC360 CA");
        using var server = CreateServerCertificate(ca, "HC360-SRV", IPAddress.Parse("192.168.1.20"));
        using var trust = new ServerTrust(ca.ExportCertificatePem());

        trust.IsTrusted(server, "attacker-laptop").ShouldBeFalse();
        trust.IsTrusted(server, "192.168.1.99").ShouldBeFalse();
    }

    [Fact]
    public void Rejects_a_certificate_from_a_different_ca_even_with_the_same_name()
    {
        using var pinned = CreateCa("CN=Nana Hospitals HC360 CA");
        using var impostor = CreateCa("CN=Nana Hospitals HC360 CA");
        using var server = CreateServerCertificate(impostor, "HC360-SRV", IPAddress.Parse("192.168.1.20"));
        using var trust = new ServerTrust(pinned.ExportCertificatePem());

        trust.IsTrusted(server, "HC360-SRV").ShouldBeFalse();
    }

    [Fact]
    public void Formats_fingerprints_in_groups_of_four() =>
        ServerTrust.Format("AB12CD34EF56").ShouldBe("AB12 CD34 EF56");

    private static X509Certificate2 CreateCa(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    private static X509Certificate2 CreateServerCertificate(X509Certificate2 ca, string host, IPAddress address)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = RandomNumberGenerator.GetBytes(12);
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
        return issued.CopyWithPrivateKey(key);
    }
}

public sealed class DiscoveryTests
{
    [Fact]
    public void Assembles_a_server_from_srv_txt_and_address_records()
    {
        var instance = new DomainName("HC360-HC360-SRV._hc360._tcp.local");
        var target = new DomainName("HC360-SRV.local");
        var txt = new TXTRecord { Name = instance };
        txt.Strings.AddRange(["ver=1.0.0", "fac=NH", $"fp={new string('A', 64)}", "host=HC360-SRV", "path=/"]);
        var records = new List<ResourceRecord>
        {
            new SRVRecord { Name = instance, Target = target, Port = 7360 },
            txt,
            new ARecord { Name = target, Address = IPAddress.Parse("192.168.1.20") },
        };

        var servers = ServerDiscovery.Assemble(records);

        var server = servers.ShouldHaveSingleItem();
        server.HostName.ShouldBe("HC360-SRV");
        server.Port.ShouldBe(7360);
        server.FacilityCode.ShouldBe("NH");
        server.CaFingerprint.ShouldBe(new string('A', 64));
        server.Addresses.ShouldContain(IPAddress.Parse("192.168.1.20"));
        server.CandidateUris().First().AbsoluteUri.ShouldBe("https://hc360-srv:7360/");
    }

    [Fact]
    public void Ignores_other_services() =>
        ServerDiscovery.Assemble([new SRVRecord { Name = new DomainName("printer._ipp._tcp.local"), Target = new DomainName("printer.local"), Port = 631 }])
            .ShouldBeEmpty();
}
