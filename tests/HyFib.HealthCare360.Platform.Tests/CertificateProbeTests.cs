using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The certificate probe checks an HC360 address from another machine as a device does: over a real TLS connection to
/// Kestrel, it finds the certificate devices would refuse (another CA, another name, expired) and judges expiry by the rule
/// System health uses, so the two never disagree.
/// </summary>
public sealed class CertificateProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_server_probed_with_the_fingerprint_the_devices_pinned_is_well()
    {
        using var hospital = Server("primary");
        await using var app = await ServeAsync(hospital.ServerCertificate, hospital.CaPem);
        var pinned = CertificateProbe.Group(hospital.CaFingerprint!);

        var result = await CertificateProbe.ProbeAsync(AddressOf(app), pinned, null, DateTimeOffset.UtcNow, Ct);

        result.Outcome.ShouldBe(CertificateProbeOutcome.Ok, string.Join(" ", result.Findings));
        (result.CaFingerprint, result.ServerExpiresAt, result.CaExpiresAt).ShouldBe((hospital.CaFingerprint, hospital.ServerExpiresAt, hospital.CaExpiresAt));
        result.Findings.ShouldHaveSingleItem().ShouldContain($"valid until {hospital.ServerExpiresAt:dd MMM yyyy}");
    }

    [Fact]
    public async Task A_server_that_hands_out_another_CA_than_the_devices_pinned_is_critical()
    {
        using var hospital = Server("primary");
        using var other = Server("other");
        await using var app = await ServeAsync(hospital.ServerCertificate, hospital.CaPem);

        var result = await CertificateProbe.ProbeAsync(AddressOf(app), other.CaFingerprint, null, DateTimeOffset.UtcNow, Ct);

        result.Outcome.ShouldBe(CertificateProbeOutcome.Critical);
        result.Findings.ShouldHaveSingleItem().ShouldContain($"not {CertificateProbe.Group(other.CaFingerprint!)}, which the devices pinned");
    }

    [Fact]
    public async Task A_certificate_another_CA_issued_is_critical()
    {
        using var hospital = Server("primary");
        using var other = Server("other");
        await using var app = await ServeAsync(other.ServerCertificate, other.CaPem);
        using var hospitalCa = X509Certificate2.CreateFromPem(hospital.CaPem);

        var againstTheHospitalCa = await CertificateProbe.ProbeAsync(AddressOf(app), null, hospitalCa, DateTimeOffset.UtcNow, Ct);
        var againstThisMachine = await CertificateProbe.ProbeAsync(AddressOf(app), null, null, DateTimeOffset.UtcNow, Ct);

        againstTheHospitalCa.Outcome.ShouldBe(CertificateProbeOutcome.Critical);
        againstTheHospitalCa.Findings.ShouldHaveSingleItem().ShouldContain("was not issued by the hospital CA");
        againstThisMachine.Outcome.ShouldBe(CertificateProbeOutcome.Critical);
        againstThisMachine.Findings.ShouldContain(f => f.Contains("This machine does not trust", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_certificate_for_another_name_is_critical()
    {
        var now = DateTimeOffset.UtcNow;
        using var ca = Ca(now.AddDays(-1), now.AddYears(10));
        using var certificate = Issue(ca, now.AddDays(397), "hc360-srv.local");
        await using var app = await ServeAsync(certificate, null);

        var result = await CertificateProbe.ProbeAsync(AddressOf(app), null, ca, now, Ct);

        result.Outcome.ShouldBe(CertificateProbeOutcome.Critical);
        result.Findings.ShouldHaveSingleItem().ShouldContain("is not for 127.0.0.1 but for hc360-srv.local");
    }

    /// <summary>The server certificate, as days pass: the rule System health uses, on what the server serves.</summary>
    [Theory]
    [InlineData(-40, CertificateProbeOutcome.Ok, "valid until")]
    [InlineData(-20, CertificateProbeOutcome.Warning, "The server certificate expires on")]
    [InlineData(-10, CertificateProbeOutcome.Critical, "and was not issued again")]
    [InlineData(1, CertificateProbeOutcome.Critical, "The server certificate expired on")]
    public async Task The_server_certificate_is_judged_as_System_health_judges_it(int daysFromExpiry, CertificateProbeOutcome outcome, string says)
    {
        using var hospital = Server("primary");
        await using var app = await ServeAsync(hospital.ServerCertificate, hospital.CaPem);

        var result = await CertificateProbe.ProbeAsync(AddressOf(app), hospital.CaFingerprint, null, hospital.ServerExpiresAt!.Value.AddDays(daysFromExpiry), Ct);

        result.Outcome.ShouldBe(outcome, string.Join(" ", result.Findings));
        result.Findings.ShouldHaveSingleItem().ShouldContain(says);
    }

    /// <summary>
    /// In the CA's last year HC360 issues the server certificate up to the CA's expiry: the probe, like System health, then
    /// speaks of the CA alone, a warning to plan with and critical in its last 90 days.
    /// </summary>
    [Theory]
    [InlineData(100, CertificateProbeOutcome.Warning, "Roll it over with 'certificates roll-ca' in the coming months")]
    [InlineData(250, CertificateProbeOutcome.Critical, "Roll it over now with 'certificates roll-ca'")]
    public async Task A_CA_in_its_last_year_is_judged_as_System_health_judges_it(int daysOn, CertificateProbeOutcome outcome, string says)
    {
        var now = DateTimeOffset.UtcNow;
        using var ca = Ca(now.AddDays(-1), now.AddDays(300));
        using var certificate = Issue(ca, ca.NotAfter, "127.0.0.1");
        await using var app = await ServeAsync(certificate, ca.ExportCertificatePem());

        var result = await CertificateProbe.ProbeAsync(AddressOf(app), CertificateProbe.Fingerprint(ca), null, now.AddDays(daysOn), Ct);

        result.Outcome.ShouldBe(outcome, string.Join(" ", result.Findings));
        result.Findings.ShouldHaveSingleItem().ShouldContain(says);
        result.CaExpiresAt.ShouldBe(new DateTimeOffset(ca.NotAfter.ToUniversalTime(), TimeSpan.Zero));
    }

    [Fact]
    public async Task An_address_nothing_answers_on_is_critical()
    {
        int port;
        using (var free = new TcpListener(IPAddress.Loopback, 0))
        {
            free.Start();
            port = ((IPEndPoint)free.LocalEndpoint).Port;
        }

        var result = await CertificateProbe.ProbeAsync(new Uri($"https://127.0.0.1:{port}/"), null, null, DateTimeOffset.UtcNow, Ct);

        result.Outcome.ShouldBe(CertificateProbeOutcome.Critical);
        result.Findings.ShouldHaveSingleItem().ShouldStartWith("No TLS connection");
    }

    /// <summary>Only the staff address hands out the CA: another address, such as the portal's, is probed with the CA's file.</summary>
    [Fact]
    public async Task An_address_that_does_not_hand_out_the_CA_is_probed_with_its_file()
    {
        using var hospital = Server("primary");
        await using var portal = await ServeAsync(hospital.ServerCertificate, caPem: null);
        using var caFile = X509Certificate2.CreateFromPem(hospital.CaPem);

        var byFingerprint = await CertificateProbe.ProbeAsync(AddressOf(portal), hospital.CaFingerprint, null, DateTimeOffset.UtcNow, Ct);
        var byFile = await CertificateProbe.ProbeAsync(AddressOf(portal), null, caFile, DateTimeOffset.UtcNow, Ct);

        byFingerprint.Outcome.ShouldBe(CertificateProbeOutcome.Critical);
        byFingerprint.Findings.ShouldHaveSingleItem().ShouldContain("give the CA's file (--ca-file)");
        byFile.Outcome.ShouldBe(CertificateProbeOutcome.Ok, string.Join(" ", byFile.Findings));
    }

    /// <summary>The CA's file as the server writes it (PEM), or as Windows exports it (DER, a .cer file).</summary>
    [Fact]
    public void A_CA_file_is_read_whether_PEM_or_DER()
    {
        using var hospital = Server("primary");
        var pem = Path.Combine(_root, "primary", "certs", "hc360-hospital-ca.crt");
        var der = Path.Combine(_root, "hospital-ca.cer");
        using (var ca = X509Certificate2.CreateFromPem(hospital.CaPem))
        {
            File.WriteAllBytes(der, ca.Export(X509ContentType.Cert));
        }

        using var fromPem = CertificateProbe.LoadCaFile(pem);
        using var fromDer = CertificateProbe.LoadCaFile(der);

        CertificateProbe.Fingerprint(fromPem).ShouldBe(hospital.CaFingerprint);
        CertificateProbe.Fingerprint(fromDer).ShouldBe(hospital.CaFingerprint);
    }

    [Fact]
    public void A_fingerprint_is_read_as_people_copy_it()
    {
        CertificateProbe.Normalise("ab:12:CD 34").ShouldBe("AB12CD34");
        CertificateProbe.Group("AB12CD34").ShouldBe("AB12 CD34");
    }

    /// <summary>A Server Node's TLS as HC360 starts it: its hospital CA, and the server certificate the CA issued.</summary>
    private LanCertificateManager Server(string machine)
    {
        var options = new PlatformOptions { DataDirectory = Path.Combine(_root, machine) };
        var certificates = new LanCertificateManager(new DataDirectories(options), options, new EphemeralDataProtectionProvider(), NullLogger<LanCertificateManager>.Instance);
        certificates.EnsureCertificates();
        return certificates;
    }

    /// <summary>
    /// A real HTTPS listener, as Kestrel serves HC360: the certificate given, and the CA where the devices read it at
    /// enrolment (the staff address; the portal's does not hand it out).
    /// </summary>
    private static async Task<WebApplication> ServeAsync(X509Certificate2 certificate, string? caPem)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        if (caPem is not null)
        {
            app.MapGet("/" + CertificateProbe.CaCertificatePath, () => Results.Text(caPem, "application/x-x509-ca-cert"));
        }

        await app.StartAsync(Ct);
        return app;
    }

    private static Uri AddressOf(WebApplication app) => new(app.Urls.Single());

    /// <summary>A CA of the test's own, made as HC360 makes its hospital CA.</summary>
    private static X509Certificate2 Ca(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test Hospital CA, O=HyFib HealthCare360", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var ca = request.CreateSelfSigned(notBefore, notAfter);
        return X509CertificateLoader.LoadPkcs12(ca.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>A server certificate <paramref name="ca"/> issued for <paramref name="names"/>, with its key, as Kestrel serves one.</summary>
    private static X509Certificate2 Issue(X509Certificate2 ca, DateTimeOffset notAfter, params string[] names)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={names[0]}, O=HyFib HealthCare360", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }

        request.CertificateExtensions.Add(san.Build());
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(ca, new DateTimeOffset(ca.NotBefore.ToUniversalTime(), TimeSpan.Zero), notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        // From a PKCS#12 file, as HC360 loads its own: Windows serves TLS only with a key it keeps, not an ephemeral one.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }
}
