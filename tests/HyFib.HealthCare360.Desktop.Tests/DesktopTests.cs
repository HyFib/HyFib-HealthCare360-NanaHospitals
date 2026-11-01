using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Desktop;
using HyFib.HealthCare360.Desktop.Bridge;
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

public sealed class BridgeSourceTests
{
    private static readonly Uri Server = new("https://hc360-srv:7360/");

    [Theory]
    [InlineData("https://hc360-srv:7360/")]
    [InlineData("https://hc360-srv:7360/front-office/queue")]
    [InlineData("https://HC360-SRV:7360/lab/worklist?status=pending")]
    [InlineData("https://hc360-srv:7360/portal-settings")]
    public void The_staff_app_may_use_the_bridge(string source) => BridgeHost.IsTrustedSource(source, Server).ShouldBeTrue();

    [Theory]
    [InlineData("https://hc360-srv:7360/portal")]
    [InlineData("https://hc360-srv:7360/portal/bills/123")]
    [InlineData("https://hc360-srv:7360/PORTAL/pay")]
    [InlineData("https://hc360-srv:7360/docs")]
    [InlineData("https://hc360-srv:7360/docs/v1")]
    [InlineData("https://evil.example/")]
    [InlineData("https://hc360-srv:8443/")]
    [InlineData("about:srcdoc")]
    [InlineData("not a url")]
    public void Pages_that_run_third_party_script_or_other_origins_may_not(string source) =>
        BridgeHost.IsTrustedSource(source, Server).ShouldBeFalse();
}

/// <summary>
/// <c>dsc.signPrescription</c> refuses anything but a prescription's canonical bytes before it opens the certificate
/// store or shows a dialog. The parsing, the certificate filter and the signature itself are tested on every OS in the
/// Clinical module's tests (PrescriptionDigitalSignatureTests); the Windows dialog and a real token are checked by hand.
/// </summary>
public sealed class DscSignerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("not base64!")]
    [InlineData("e30=")] // {}
    [InlineData("eyJmb3JtYXQiOiJvdGhlciJ9")] // {"format":"other"}
    public void Anything_but_a_prescription_is_refused_before_a_dialog_opens(string? content) =>
        Should.Throw<BridgeException>(() => DscSigner.Sign(content)).Code.ShouldBe("bad_request");
}

public sealed class DownloadPolicyTests
{
    private static readonly Uri Server = new("https://hc360-srv:7360/");

    [Theory]
    [InlineData("https://hc360-srv:7360/api/v1/files/0199?download=true", @"C:\Users\nurse\Downloads\discharge-summary.pdf")]
    [InlineData("https://hc360-srv:7360/api/v1/analytics/reports/opd/export", @"C:\Users\nurse\Downloads\opd.xlsx")]
    [InlineData("blob:https://hc360-srv:7360/5b1d2c9e-0000-4000-8000-000000000001", @"C:\Users\nurse\Downloads\pharmacy-register.csv")]
    [InlineData("data:text/csv;charset=utf-8,a,b", @"C:\Users\nurse\Downloads\morbidity.csv")]
    [InlineData("https://hc360-srv:7360/api/v1/system/ca-certificate", @"C:\Users\it\Downloads\hc360-hospital-ca.crt")]
    [InlineData("blob:https://hc360-srv:7360/5b1d2c9e-0000-4000-8000-000000000002", @"C:\Users\tech\Downloads\ecg-0412.scp")]
    [InlineData("blob:https://hc360-srv:7360/5b1d2c9e-0000-4000-8000-000000000003", @"C:\Users\doctor\Downloads\prescription-NHRX260000123.p7s")]
    [InlineData("blob:https://hc360-srv:7360/5b1d2c9e-0000-4000-8000-000000000004", @"C:\Users\doctor\Downloads\prescription-NHRX260000123.json")]
    public void Documents_from_the_hospital_server_are_saved(string source, string path) =>
        DownloadPolicy.IsAllowed(source, path, Server).ShouldBeTrue();

    [Theory]
    [InlineData("https://hc360-srv:7360/api/v1/files/0199?download=true", @"C:\Users\nurse\Downloads\report.hta")]
    [InlineData("https://hc360-srv:7360/api/v1/files/0199?download=true", @"C:\Users\nurse\Downloads\report.pdf.exe")]
    [InlineData("blob:https://hc360-srv:7360/5b1d2c9e-0000-4000-8000-000000000001", @"C:\Users\nurse\Downloads\run.ps1")]
    [InlineData("https://hc360-srv:7360/portal", @"C:\Users\nurse\Downloads\bill.html")]
    [InlineData("https://evil.example/invoice.pdf", @"C:\Users\nurse\Downloads\invoice.pdf")]
    [InlineData("blob:https://evil.example/5b1d2c9e-0000-4000-8000-000000000001", @"C:\Users\nurse\Downloads\invoice.pdf")]
    [InlineData("http://hc360-srv:7360/api/v1/files/0199", @"C:\Users\nurse\Downloads\scan.pdf")]
    public void Programs_scripts_and_other_sites_are_refused(string source, string path) =>
        DownloadPolicy.IsAllowed(source, path, Server).ShouldBeFalse();
}

public sealed class FingerprintCheckTests
{
    private const string Presented = "AB12";
    private const string Other = "CD34";

    // The verdict is internal to the app, so the theory names it.
    [Theory]
    [InlineData(Presented, null, nameof(FingerprintVerdict.MatchesPolicy))]
    [InlineData(Presented, Other, nameof(FingerprintVerdict.MatchesPolicy))]
    [InlineData(Other, null, nameof(FingerprintVerdict.ContradictsPolicy))]
    [InlineData(Other, Presented, nameof(FingerprintVerdict.ContradictsPolicy))]
    public void The_fingerprint_it_pinned_decides_whatever_the_network_announced(string policy, string? announced, string expected) =>
        SetupWindow.CheckFingerprint(Presented, policy, announced).ToString().ShouldBe(expected);

    [Fact]
    public void Without_a_policy_a_matching_announcement_is_shown_but_a_person_still_confirms() =>
        SetupWindow.CheckFingerprint(Presented, null, Presented).ShouldBe(FingerprintVerdict.MatchesAnnouncement);

    [Fact]
    public void A_certificate_that_contradicts_the_announcement_is_refused_rather_than_left_to_a_warning() =>
        SetupWindow.CheckFingerprint(Presented, null, Other).ShouldBe(FingerprintVerdict.ContradictsAnnouncement);

    [Fact]
    public void With_nothing_to_compare_a_person_compares_it_with_the_server() =>
        SetupWindow.CheckFingerprint(Presented, null, null).ShouldBe(FingerprintVerdict.Unverified);
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
    public void Trusts_a_server_certificate_from_an_intermediate_the_server_sends()
    {
        using var ca = CreateCa("CN=Nana Hospitals HC360 CA");
        using var intermediate = CreateIntermediate(ca, "CN=Nana Hospitals LAN CA");
        using var server = CreateServerCertificate(intermediate, "HC360-SRV", IPAddress.Parse("192.168.1.20"));
        using var trust = new ServerTrust(ca.ExportCertificatePem());

        trust.IsTrusted(server, "HC360-SRV", [intermediate]).ShouldBeTrue();
        trust.IsTrusted(server, "HC360-SRV").ShouldBeFalse();
    }

    [Fact]
    public void Rejects_a_certificate_that_is_not_for_a_tls_server()
    {
        using var ca = CreateCa("CN=Nana Hospitals HC360 CA");
        var clientAuthentication = "1.3.6.1.5.5.7.3.2";
        using var server = CreateServerCertificate(ca, "HC360-SRV", IPAddress.Parse("192.168.1.20"), clientAuthentication);
        using var trust = new ServerTrust(ca.ExportCertificatePem());

        trust.IsTrusted(server, "HC360-SRV").ShouldBeFalse();
    }

    [Fact]
    public void Reports_a_certificate_it_refuses()
    {
        using var pinned = CreateCa("CN=Nana Hospitals HC360 CA");
        using var proxy = CreateCa("CN=Inspection Proxy CA");
        using var forged = CreateServerCertificate(proxy, "HC360-SRV", IPAddress.Parse("192.168.1.20"));
        using var trust = new ServerTrust(pinned.ExportCertificatePem());
        var rejected = new List<RejectedCertificate>();
        trust.CertificateRejected += rejected.Add;

        trust.Check(forged, null, "HC360-SRV").ShouldBeFalse();

        var report = rejected.ShouldHaveSingleItem();
        report.Host.ShouldBe("HC360-SRV");
        report.Issuer.ShouldBe("CN=Inspection Proxy CA");
        report.Fingerprint.ShouldBe(Convert.ToHexString(SHA256.HashData(forged.RawData)));
    }

    [Fact]
    public void Reports_nothing_for_the_hospital_certificate()
    {
        using var ca = CreateCa("CN=Nana Hospitals HC360 CA");
        using var server = CreateServerCertificate(ca, "HC360-SRV", IPAddress.Parse("192.168.1.20"));
        using var trust = new ServerTrust(ca.ExportCertificatePem());
        var rejected = 0;
        trust.CertificateRejected += _ => rejected++;

        trust.Check(server, null, "HC360-SRV").ShouldBeTrue();

        rejected.ShouldBe(0);
    }

    [Fact]
    public async Task A_pinned_request_refuses_and_reports_a_server_that_presents_another_certificate()
    {
        // A TLS-inspection proxy answers with a certificate of its own, from a CA that Windows may well trust.
        using var pinned = CreateCa("CN=Nana Hospitals HC360 CA");
        using var proxy = CreateCa("CN=Inspection Proxy CA");
        using var forged = UsableForTls(CreateServerCertificate(proxy, "localhost", IPAddress.Loopback));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serving = ServeOnceAsync(listener, forged);
            using var trust = new ServerTrust(pinned.ExportCertificatePem());
            var rejected = new List<RejectedCertificate>();
            trust.CertificateRejected += report =>
            {
                lock (rejected)
                {
                    rejected.Add(report);
                }
            };
            using var http = trust.CreateHttpClient(
                new Uri($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/"), TimeSpan.FromSeconds(10));

            await Should.ThrowAsync<HttpRequestException>(
                () => http.GetAsync(new Uri("health/live", UriKind.Relative), TestContext.Current.CancellationToken));

            rejected.ShouldHaveSingleItem().Fingerprint.ShouldBe(ServerTrust.ComputeFingerprint(forged));
            await serving;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Formats_fingerprints_in_groups_of_four() =>
        ServerTrust.Format("AB12CD34EF56").ShouldBe("AB12 CD34 EF56");

    /// <summary>
    /// Accepts one connection and offers <paramref name="certificate"/>; the client is expected to refuse it.
    /// </summary>
    private static async Task ServeOnceAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await using var tls = new SslStream(client.GetStream());
        try
        {
            await tls.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions { ServerCertificate = certificate },
                TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException)
        {
            // The client closed the handshake when it refused the certificate.
        }
    }

    /// <summary>A copy whose private key Windows TLS can use (it cannot use an in-memory key).</summary>
    internal static X509Certificate2 UsableForTls(X509Certificate2 certificate)
    {
        using (certificate)
        {
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
        }
    }

    internal static X509Certificate2 CreateCa(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    private static X509Certificate2 CreateIntermediate(X509Certificate2 ca, string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var serial = RandomNumberGenerator.GetBytes(12);
        var notAfter = DateTimeOffset.UtcNow.AddYears(2);
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), notAfter, serial);
        return issued.CopyWithPrivateKey(key);
    }

    internal static X509Certificate2 CreateServerCertificate(
        X509Certificate2 ca, string host, IPAddress address, string usage = "1.3.6.1.5.5.7.3.1")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        var serial = RandomNumberGenerator.GetBytes(12);
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
        return issued.CopyWithPrivateKey(key);
    }
}

public sealed class NewWindowPolicyTests
{
    private static readonly Uri Server = new("https://hc360-srv:7360/");

    [Fact]
    public void Opens_a_page_of_the_server_in_another_window_so_a_video_call_keeps_its_own() =>
        NewWindowPolicy.Decide("https://HC360-SRV:7360/emr/patients/p1", Server, kiosk: false, locked: false)
            .ShouldBe(NewWindowAction.OpenWindow);

    [Fact]
    public void A_kiosk_keeps_one_window() =>
        NewWindowPolicy.Decide("https://hc360-srv:7360/display/queue", Server, kiosk: true, locked: false)
            .ShouldBe(NewWindowAction.OpenHere);

    [Fact]
    public void Opens_nothing_of_the_server_while_the_window_is_locked() =>
        NewWindowPolicy.Decide("https://hc360-srv:7360/emr/patients/p1", Server, kiosk: false, locked: true)
            .ShouldBe(NewWindowAction.Refuse);

    [Theory]
    [InlineData("https://example.org/help")]
    [InlineData("https://hc360-srv:7443/emr")]
    [InlineData("http://hc360-srv:7360/emr")]
    public void Sends_other_sites_to_the_system_browser(string address) =>
        NewWindowPolicy.Decide(address, Server, kiosk: false, locked: false).ShouldBe(NewWindowAction.OpenExternally);

    [Fact]
    public void Refuses_an_address_that_is_not_one() =>
        NewWindowPolicy.Decide("not an address", Server, kiosk: false, locked: false).ShouldBe(NewWindowAction.Refuse);
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
        server.PreviousCaFingerprint.ShouldBeNull("no rollover is announced");
    }

    [Fact]
    public void Reads_the_CA_a_rolled_over_server_replaced_from_its_txt_record()
    {
        var instance = new DomainName("HC360-HC360-SRV._hc360._tcp.local");
        var target = new DomainName("HC360-SRV.local");
        var txt = new TXTRecord { Name = instance };
        txt.Strings.AddRange(["ver=1.0.0", "fac=NH", $"fp={new string('C', 64)}", "host=HC360-SRV", "path=/", $"fpprev={new string('a', 64)}"]);

        var server = ServerDiscovery.Assemble([new SRVRecord { Name = instance, Target = target, Port = 7360 }, txt]).ShouldHaveSingleItem();

        (server.CaFingerprint, server.PreviousCaFingerprint).ShouldBe((new string('C', 64), new string('A', 64)));
    }

    [Fact]
    public async Task Finds_the_server_on_this_computer_when_something_listens_on_its_port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var server = await ServerDiscovery.ThisComputerAsync(port, TestContext.Current.CancellationToken);

            server.ShouldNotBeNull().OnThisComputer.ShouldBeTrue();
            server.Display.ShouldBe($"This computer · {Environment.MachineName} (all-in-one)");
            var machine = Environment.MachineName.ToLowerInvariant();
            server.CandidateUris().First().AbsoluteUri.ShouldBe($"https://{machine}:{port}/");
            server.CandidateUris().Last().AbsoluteUri.ShouldBe($"https://127.0.0.1:{port}/");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Finds_no_server_on_this_computer_when_nothing_listens()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        (await ServerDiscovery.ThisComputerAsync(port, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public void Ignores_other_services() =>
        ServerDiscovery.Assemble([new SRVRecord { Name = new DomainName("printer._ipp._tcp.local"), Target = new DomainName("printer.local"), Port = 631 }])
            .ShouldBeEmpty();
}
