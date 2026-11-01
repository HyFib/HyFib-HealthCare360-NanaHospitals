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
/// hospital CA, and each step of a rollover, are made only once confirmed, and the probe's exit code is the worst of the
/// addresses it checks.
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

    /// <summary>
    /// The rollover's two steps each wait for --confirm, and the switch for every device seen in the last 30 days to hold the
    /// next CA, as the database says: here what it says is given, and CaRolloverDeviceTests reads it from a real one. Neither
    /// runs twice: the next CA is prepared once, and switched to once.
    /// </summary>
    [Fact]
    public async Task The_next_CA_is_prepared_and_switched_to_only_once_confirmed_and_the_switch_waits_for_every_device()
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        var keys = HospitalDataProtection.CreateNodeProvider(directories);
        string current;
        using (var first = Start(directories))
        {
            current = first.CaFingerprint!;
        }

        (await ServerCli.RunAsync(["certificates", "prepare-ca", "--data-dir", _root])).ShouldBe(1, "nothing is prepared without --confirm");
        LanCertificateManager.PreparedNextCaFingerprint(directories, keys, DateTimeOffset.UtcNow).ShouldBeNull();
        (await ServerCli.RunAsync(["certificates", "prepare-ca", "--confirm", "--data-dir", _root])).ShouldBe(0);
        var next = LanCertificateManager.PreparedNextCaFingerprint(directories, keys, DateTimeOffset.UtcNow).ShouldNotBeNull();
        Fingerprint(directories).ShouldBe(current, "the server keeps serving with its CA");
        (await ServerCli.RunAsync(["certificates", "prepare-ca", "--confirm", "--data-dir", _root])).ShouldBe(1, "prepared already");
        LanCertificateManager.PreparedNextCaFingerprint(directories, keys, DateTimeOffset.UtcNow).ShouldBe(next);

        var options = new Dictionary<string, string> { ["data-dir"] = _root };
        var ready = new CaRolloverReadiness([], [], 3);
        (await ServerCli.SwitchCaAsync(options, confirmed: true, (_, _) => Task.FromResult(new CaRolloverReadiness(["Lab PC 2"], [], 3))))
            .ShouldBe(1, "a device without the next CA would no longer trust the server");
        (await ServerCli.SwitchCaAsync(options, confirmed: true, (_, _) => Task.FromResult(new CaRolloverReadiness([], ["Ward 3 Station"], 3))))
            .ShouldBe(1, "nor might one that does not say");
        (await ServerCli.SwitchCaAsync(options, confirmed: true, (_, _) => Task.FromException<CaRolloverReadiness>(new TimeoutException("no answer"))))
            .ShouldBe(3, "without the database it cannot tell");
        (await ServerCli.SwitchCaAsync(options, confirmed: false, (_, _) => Task.FromResult(ready))).ShouldBe(1, "nothing is switched without --confirm");
        Fingerprint(directories).ShouldBe(current);

        string? askedFor = null;
        (await ServerCli.SwitchCaAsync(options, confirmed: true, (fingerprint, _) =>
        {
            askedFor = fingerprint;
            return Task.FromResult(ready);
        })).ShouldBe(0);
        askedFor.ShouldBe(next, "it asks which devices hold the next CA");
        Fingerprint(directories).ShouldBe(next);
        Directory.GetFiles(directories.Certificates, "hospital-ca.pfx.protected.replaced-*").ShouldHaveSingleItem("the CA it replaced is kept");
        (await ServerCli.SwitchCaAsync(options, confirmed: true, (_, _) => Task.FromResult(ready))).ShouldBe(1, "no next CA is prepared any more");

        using var started = Start(directories);
        (started.CaFingerprint, started.PreviousCaFingerprint).ShouldBe((next, current), "the server starts with the next CA, and hands out the one it replaced");
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
