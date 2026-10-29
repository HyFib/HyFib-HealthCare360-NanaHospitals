using System.Net;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The certificate commands as an administrator and a monitoring tool run them (docs/deployment/CA-ROLLOVER.md): a new
/// hospital CA is made only once confirmed, and the probe's exit code is the worst of the addresses it checks.
/// </summary>
public sealed class CertificateCommandTests : IDisposable
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
    public async Task A_new_CA_is_made_only_once_confirmed()
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        using (var first = Start(directories))
        {
            var replaced = first.CaFingerprint;

            (await ServerCli.RunAsync(["certificates", "new-ca", "--data-dir", _root])).ShouldBe(1, "nothing is replaced without --confirm");
            Fingerprint(directories).ShouldBe(replaced);

            (await ServerCli.RunAsync(["certificates", "new-ca", "--confirm", "--data-dir", _root])).ShouldBe(0);
            Fingerprint(directories).ShouldNotBe(replaced);
            Directory.GetFiles(directories.Certificates, "hospital-ca.pfx.protected.replaced-*").ShouldHaveSingleItem();
        }

        using var started = Start(directories);
        started.CaFingerprint.ShouldBe(Fingerprint(directories), "the server starts with the new CA");
    }

    [Fact]
    public async Task The_probe_exits_with_the_worst_of_the_addresses_it_checks()
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        using var server = Start(directories);
        await using var app = await ServeAsync(server.ServerCertificate, server.CaPem!);
        var address = app.Urls.Single();
        var caFile = Path.Combine(directories.Certificates, "hc360-hospital-ca.crt");
        var pinned = CertificateProbe.Group(server.CaFingerprint!);

        (await ServerCli.RunAsync(["certificates", "probe", address, "--ca-fingerprint", pinned, "--event-log"])).ShouldBe(0);
        (await ServerCli.RunAsync(["certificates", "probe", "--ca-file", caFile, address])).ShouldBe(0, "the addresses may follow the options");
        (await ServerCli.RunAsync(["certificates", "probe", address, "--ca-fingerprint", new string('A', 64)])).ShouldBe(2, "another CA than the one pinned");
        (await ServerCli.RunAsync(["certificates", "probe", address, "http://hc360-srv:7360/", "--ca-file", caFile])).ShouldBe(2, "one address that cannot be checked");
        (await ServerCli.RunAsync(["certificates", "probe", address, "--ca-fingerprint", "AB12"])).ShouldBe(2, "a fingerprint too short to be one");
    }

    /// <summary>The server's TLS as it starts, with the node key ring the commands open the CA with.</summary>
    private static LanCertificateManager Start(DataDirectories directories)
    {
        var certificates = new LanCertificateManager(
            directories, new PlatformOptions { DataDirectory = directories.Root }, HospitalDataProtection.CreateNodeProvider(directories),
            NullLogger<LanCertificateManager>.Instance);
        certificates.EnsureCertificates();
        return certificates;
    }

    private static string Fingerprint(DataDirectories directories)
    {
        using var ca = LanCertificateManager.PublicCa(directories).ShouldNotBeNull();
        return CertificateProbe.Fingerprint(ca);
    }

    /// <summary>A real HTTPS listener, as Kestrel serves HC360's staff address, handing out the CA as the devices read it.</summary>
    private static async Task<WebApplication> ServeAsync(X509Certificate2 certificate, string caPem)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.MapGet("/" + CertificateProbe.CaCertificatePath, () => Results.Text(caPem, "application/x-x509-ca-cert"));
        await app.StartAsync(Ct);
        return app;
    }
}
