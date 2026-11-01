using System.Net;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// After the hospital CA is rolled over ('certificates roll-ca'), HC360 Desktop and the lab agent take the new CA by
/// themselves, on the word of the CA they pin (<see cref="HospitalCaPins"/>): over a connection pinned to the old CA they
/// fetch the server's CA and keep what the server sent with its own certificate, which holds the certificate the old CA
/// signed for the new key. Over real TLS to Kestrel, as HC360 serves it (<see cref="HospitalHttps"/>), with the code both
/// apps check the server with; their own few lines around it are tested on Windows.
/// </summary>
public sealed class HospitalCaFollowTests : IDisposable
{
    /// <summary>When the old CA was made: nine and a half years ago, so it is in its last year, as a CA is when it is rolled over.</summary>
    private static readonly DateTimeOffset Made = DateTimeOffset.UtcNow.AddYears(-9).AddMonths(-6);

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
    public async Task A_device_that_pins_the_old_CA_follows_the_new_one_on_its_word_and_keeps_the_old_one_as_a_second_pin()
    {
        var (directories, keys) = Machine("primary");
        string oldPem;
        using (var first = Start(directories, keys, Made))
        {
            oldPem = first.CaPem!;
        }

        // A standby that never took the rollover: the server as it was before it.
        var standbyDirectories = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, "standby") });
        CopyDirectory(directories.Root, standbyDirectories.Root);

        var now = DateTimeOffset.UtcNow;
        var rolled = LanCertificateManager.RollCa(directories, keys, now);
        using var server = Start(directories, keys, now);
        await using var rolledServer = await ServeAsync(server);
        using var oldCa = X509Certificate2.CreateFromPem(oldPem);
        using var newCa = X509Certificate2.CreateFromPem(server.CaPem!);
        using var pins = new HospitalCaPins(oldPem);

        var successor = (await AskAsync(rolledServer, pins, now)).ShouldNotBeNull("the old CA vouched for the new one in the handshake");
        successor.Fingerprint.ShouldBe(rolled.Fingerprint);
        using (var followed = X509Certificate2.CreateFromPem(successor.CaPem))
        using (var cross = X509Certificate2.CreateFromPem(successor.CrossPem))
        {
            followed.RawData.ShouldBe(newCa.RawData);
            HospitalCaSuccession.Succeeded(oldCa, newCa, cross).ShouldBeTrue("it keeps the certificate the old CA signed for the new key");
        }

        pins.Follow(successor);
        pins.Fingerprint.ShouldBe(rolled.Fingerprint);
        pins.Fingerprints(now).ShouldBe([rolled.Fingerprint, rolled.PreviousFingerprint], "the heartbeat says both, the one it pins first");
        pins.Fingerprints(rolled.PreviousExpiresAt.AddDays(1)).ShouldBe([rolled.Fingerprint], "an expired CA vouches for nothing");
        (await AskAsync(rolledServer, pins, now)).ShouldBeNull("nothing more to follow: the server's CA is the one it pins");

        // Once the old CA expires, the server sends no certificate it signed: the device that followed holds the new CA. (Windows'
        // TLS sends what it finds in this computer's stores, where HC360 put it: out of them first.)
        WindowsTlsChain.Withdraw(server.SentWithCertificate);
        await using (var afterTheOldCa = await ServeAsync(server, leafAlone: true))
        {
            (await TrustsAsync(afterTheOldCa, pins)).ShouldBeTrue();
            using var onlyTheOld = new HospitalCaPins(oldPem);
            (await TrustsAsync(afterTheOldCa, onlyTheOld)).ShouldBeFalse("one that did not follow is cut off then");
        }

        // A standby still on the old CA, until it is given the rollover: the second pin still trusts it, the new CA alone does
        // not, and the device never goes back to the old CA.
        using var stale = Start(standbyDirectories, keys, now);
        await using var standby = await ServeAsync(stale);
        (await TrustsAsync(standby, pins)).ShouldBeTrue("the old CA, a second pin");
        using var onlyTheNew = new HospitalCaPins(server.CaPem!);
        (await TrustsAsync(standby, onlyTheNew)).ShouldBeFalse();
        (await AskAsync(standby, pins, now)).ShouldBeNull("never back to the CA it replaced");
    }

    /// <summary>
    /// A CA made again ('certificates new-ca', for a key that may have been seen) has no word from the old one: a device that
    /// pins the old CA is refused by the server's certificate, and asks nothing of it. It is set up again.
    /// </summary>
    [Fact]
    public async Task A_CA_made_again_rather_than_rolled_over_is_never_followed()
    {
        var (directories, keys) = Machine("primary");
        string oldPem;
        using (var first = Start(directories, keys, Made))
        {
            oldPem = first.CaPem!;
        }

        LanCertificateManager.NewCa(directories, keys, "Nana Hospitals", DateTimeOffset.UtcNow);
        using var replaced = Start(directories, keys, DateTimeOffset.UtcNow);
        await using var server = await ServeAsync(replaced);
        using var pins = new HospitalCaPins(oldPem);

        await Should.ThrowAsync<HttpRequestException>(() => AskAsync(server, pins, DateTimeOffset.UtcNow));
        pins.Fingerprint.ShouldNotBe(replaced.CaFingerprint);
    }

    /// <summary>
    /// A certificate this computer holds, but the server did not send, does not count: the chain is built from what the server
    /// sent and the pins only. Windows builds chains from the computer's own stores, and .NET elsewhere from the user's: here,
    /// the certificate the old CA signed is in this user's intermediate store while the server sends its certificate alone.
    /// </summary>
    [Fact]
    public void A_certificate_on_this_computer_that_the_server_did_not_send_does_not_count()
    {
        var (directories, keys) = Machine("primary");
        string oldPem;
        using (var first = Start(directories, keys, Made))
        {
            oldPem = first.CaPem!;
        }

        LanCertificateManager.RollCa(directories, keys, DateTimeOffset.UtcNow);
        using var server = Start(directories, keys, DateTimeOffset.UtcNow);
        using var cross = X509CertificateLoader.LoadCertificate(server.SentWithCertificate[0].RawData);
        using var pins = new HospitalCaPins(oldPem);
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cross);
        try
        {
            pins.IsTrusted(server.ServerCertificate, "127.0.0.1", []).ShouldBeFalse("the server sent its certificate alone");
            pins.IsTrusted(server.ServerCertificate, "127.0.0.1", [cross]).ShouldBeTrue("the server sent the certificate the old CA signed");
        }
        finally
        {
            store.Remove(cross);
        }
    }

    /// <summary>A second pin that cannot be read is left out: the device still trusts the server through the first.</summary>
    [Fact]
    public void A_second_pin_that_cannot_be_read_is_left_out()
    {
        var (directories, keys) = Machine("primary");
        using var server = Start(directories, keys, DateTimeOffset.UtcNow);
        using var pins = new HospitalCaPins(server.CaPem!, "-----BEGIN CERTIFICATE-----\nnot a certificate\n-----END CERTIFICATE-----");

        pins.PreviousCa.ShouldBeNull();
        pins.Fingerprints(DateTimeOffset.UtcNow).ShouldBe([server.CaFingerprint!]);
    }

    /// <summary>
    /// What HC360 Desktop and the lab agent do every six hours: over a connection pinned with <paramref name="pins"/>, ask the
    /// server for its CA, keeping what the server sent with its certificate, and see whether the pinned CA vouched for it.
    /// Throws <see cref="HttpRequestException"/> when the device refuses the server.
    /// </summary>
    private static async Task<HospitalCaSuccessor?> AskAsync(WebApplication server, HospitalCaPins pins, DateTimeOffset now)
    {
        var address = new Uri(server.Urls.Single());
        X509Certificate2[] sent = [];
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                {
                    sent = HospitalCaPins.KeepSent(certificate, chain);
                    return pins.Validate(certificate, chain, address.Host);
                },
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = address };
        var pem = await http.GetStringAsync(CertificateProbe.CaCertificatePath, Ct);
        try
        {
            return pins.SuccessorIn(pem, sent, now);
        }
        finally
        {
            foreach (var certificate in sent)
            {
                certificate.Dispose();
            }
        }
    }

    private static async Task<bool> TrustsAsync(WebApplication server, HospitalCaPins pins)
    {
        try
        {
            await AskAsync(server, pins, DateTimeOffset.UtcNow);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// A real HTTPS listener with HC360's TLS (<see cref="HospitalHttps"/>), handing out the CA where the devices read it.
    /// <paramref name="leafAlone"/> serves the server's certificate alone, as the server does once the old CA expired.
    /// </summary>
    private static async Task<WebApplication> ServeAsync(LanCertificateManager certificates, bool leafAlone = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            if (leafAlone)
            {
                listen.UseHttps(certificates.ServerCertificate);
            }
            else
            {
                listen.UseHttps(https => HospitalHttps.Configure(https, certificates));
            }
        }));
        var app = builder.Build();
        app.MapGet("/" + CertificateProbe.CaCertificatePath, () => Results.Text(certificates.CaPem!, "application/x-x509-ca-cert"));
        await app.StartAsync(Ct);
        return app;
    }

    private (DataDirectories Directories, IDataProtectionProvider Keys) Machine(string name) =>
        (new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, name) }), new EphemeralDataProtectionProvider());

    /// <summary>Starts the server's TLS as HC360 does at <paramref name="now"/>.</summary>
    private static LanCertificateManager Start(DataDirectories directories, IDataProtectionProvider keys, DateTimeOffset now)
    {
        var certificates = new LanCertificateManager(
            directories, new PlatformOptions { DataDirectory = directories.Root }, keys, NullLogger<LanCertificateManager>.Instance, new Clock(now));
        certificates.EnsureCertificates();
        return certificates;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(from, to, StringComparison.Ordinal));
        }

        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal));
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
