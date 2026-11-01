using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Discovery;
using Makaretu.Dns;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A planned rollover of the hospital CA ('certificates roll-ca', docs/deployment/CA-ROLLOVER.md) sets no device up again:
/// the new CA has the old one's name and a new key, the old CA signs a certificate for the new key, and the server sends it
/// with its own until the old CA expires. Over real TLS to Kestrel, as HC360 serves it (<see cref="HospitalHttps"/>), a
/// device that pins the old CA, one that pins the new CA and one that pins another CA each check the server as HC360
/// Desktop and the lab agent do (their ServerTrust, whose own tests run on Windows).
/// </summary>
public sealed class HospitalCaRollTests : IDisposable
{
    private const string Password = "standby-transfer-2026";

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
    public void A_rollover_keeps_the_CA_name_makes_a_new_key_and_has_the_old_CA_vouch_for_it_until_it_expires()
    {
        var (directories, keys) = Machine("primary");
        var now = DateTimeOffset.UtcNow;
        var old = Snapshot(directories, keys, Made);

        var rolled = LanCertificateManager.RollCa(directories, keys, now);

        rolled.PreviousFingerprint.ShouldBe(old.CaFingerprint);
        rolled.Fingerprint.ShouldNotBe(old.CaFingerprint);
        (rolled.PreviousExpiresAt, rolled.CrossSignedUntil).ShouldBe((old.CaExpiresAt, old.CaExpiresAt), "the old CA vouches for the new one until it expires");
        rolled.ExpiresAt.ShouldBeGreaterThan(now.AddYears(10).AddDays(-2));
        File.Exists(Path.Combine(directories.Certificates, rolled.PreviousKeptAs.ShouldNotBeNull())).ShouldBeTrue("the old CA is kept, never deleted");
        File.Exists(Path.Combine(directories.Certificates, "server.pfx.protected")).ShouldBeFalse("the server certificate the old CA issued goes with it");

        using var server = Start(directories, keys, now);
        server.CaFingerprint.ShouldBe(rolled.Fingerprint);
        (server.PreviousCaFingerprint, server.CrossSignedUntil).ShouldBe((old.CaFingerprint, old.CaExpiresAt));
        using var oldCa = X509Certificate2.CreateFromPem(old.CaPem);
        using var newCa = X509Certificate2.CreateFromPem(server.CaPem!);
        using var previous = X509Certificate2.CreateFromPem(server.PreviousCaPem!);
        CertificateProbe.Fingerprint(previous).ShouldBe(old.CaFingerprint, "the server hands out the CA it replaced while it vouches for the new one");
        newCa.SubjectName.RawData.ShouldBe(oldCa.SubjectName.RawData, "the same name, so the certificate the old CA signs for it is self-issued");
        newCa.PublicKey.EncodedKeyValue.RawData.ShouldNotBe(oldCa.PublicKey.EncodedKeyValue.RawData, "a new key");
        newCa.Extensions.OfType<X509BasicConstraintsExtension>().Single().PathLengthConstraint.ShouldBe(0, "it issues server certificates only, as the old one");

        // What the server sends after its own certificate: the certificate the old CA signed for the new key, and the old CA it
        // leads to; never the new CA, which the devices that moved hold themselves. (What goes over the wire: the TLS test.)
        server.ServerCertificateContext.ShouldNotBeNull();
        var cross = Crosses(server.SentWithCertificate, newCa).ShouldHaveSingleItem();
        server.SentWithCertificate.Select(c => c.RawData).ShouldBe([cross.RawData, oldCa.RawData]);
        if (OperatingSystem.IsWindows())
        {
            WindowsTlsChain.Holds(cross).ShouldBeTrue("Windows' TLS sends only what it finds in this computer's stores");
        }
        cross.SubjectName.RawData.ShouldBe(newCa.SubjectName.RawData);
        cross.IssuerName.RawData.ShouldBe(oldCa.SubjectName.RawData, "the old CA's name, which is the new one's: self-issued");
        cross.PublicKey.EncodedKeyValue.RawData.ShouldBe(newCa.PublicKey.EncodedKeyValue.RawData);
        cross.NotAfter.ShouldBe(oldCa.NotAfter, "valid as long as the old CA, and no longer");
        cross.Extensions.OfType<X509BasicConstraintsExtension>().Single().PathLengthConstraint.ShouldBe(0);
        cross.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Single().KeyIdentifier!.Value.ToArray()
            .ShouldBe(oldCa.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifierBytes.ToArray(), "it names the old key as its issuer's");
    }

    [Fact]
    public async Task Over_TLS_devices_that_pin_the_old_CA_or_the_new_one_trust_the_rolled_server_and_one_that_pins_another_CA_does_not()
    {
        var (directories, keys) = Machine("primary");
        var old = Snapshot(directories, keys, Made);
        var stranger = Snapshot(Machine("other-hospital"), DateTimeOffset.UtcNow);
        LanCertificateManager.RollCa(directories, keys, DateTimeOffset.UtcNow);
        using var server = Start(directories, keys, DateTimeOffset.UtcNow);
        await using var app = await ServeAsync(server);

        var pinnedToOld = await ConnectAsync(app, old.CaPem);
        var pinnedToNew = await ConnectAsync(app, server.CaPem!);
        var pinnedToAnother = await ConnectAsync(app, stranger.CaPem);

        pinnedToOld.Trusted.ShouldBeTrue("a device that has not moved yet trusts the server through the certificate the old CA signed");
        pinnedToNew.Trusted.ShouldBeTrue("a device that moved trusts the server through the new CA itself");
        pinnedToAnother.Trusted.ShouldBeFalse("another CA is trusted no more after a rollover than before");

        // What went over the wire after the server's own certificate: the certificate the old CA signed for the new key, and
        // nothing else but, where the platform sends the root too, the old CA. Never the new CA.
        using var newCa = X509Certificate2.CreateFromPem(server.CaPem!);
        Crosses(pinnedToOld.Sent, newCa).ShouldHaveSingleItem();
        pinnedToOld.Sent.ShouldAllBe(c => Crosses(new[] { c }, newCa).Any() || CertificateProbe.Fingerprint(c) == old.CaFingerprint);
    }

    /// <summary>Why the server sends it: served alone, as Kestrel served the certificate before, the old CA does not reach it.</summary>
    [Fact]
    public async Task Without_the_certificate_the_old_CA_signed_a_device_that_pins_the_old_CA_refuses_the_rolled_server()
    {
        var (directories, keys) = Machine("primary");
        var old = Snapshot(directories, keys, Made);
        LanCertificateManager.RollCa(directories, keys, DateTimeOffset.UtcNow);
        using var server = Start(directories, keys, DateTimeOffset.UtcNow);

        // Windows' TLS sends what it finds in this computer's stores, where HC360 put the certificate: out of them, so the
        // server certificate goes out alone, as it did before.
        WindowsTlsChain.Withdraw(server.SentWithCertificate);
        await using var alone = await ServeAsync(server, leafAlone: true);

        var pinnedToOld = await ConnectAsync(alone, old.CaPem);
        pinnedToOld.Sent.ShouldBeEmpty("the server certificate alone");
        pinnedToOld.Trusted.ShouldBeFalse();
        (await ConnectAsync(alone, server.CaPem!)).Trusted.ShouldBeTrue();
    }

    [Fact]
    public async Task A_standby_given_the_rolled_CA_sends_the_same_chain_so_devices_that_pin_the_old_CA_trust_it_too()
    {
        var (primary, primaryKeys) = Machine("primary");
        var old = Snapshot(primary, primaryKeys, Made);
        var rolled = LanCertificateManager.RollCa(primary, primaryKeys, DateTimeOffset.UtcNow);

        var exported = LanCertificateManager.ExportCa(primary, primaryKeys, Password);
        var (standby, standbyKeys) = Machine("standby");
        Snapshot(standby, standbyKeys, DateTimeOffset.UtcNow);
        var imported = LanCertificateManager.ImportCa(standby, standbyKeys, exported.Pkcs12, Password, DateTimeOffset.UtcNow);

        (exported.Fingerprint, exported.CrossSignedUntil).ShouldBe((rolled.Fingerprint, rolled.CrossSignedUntil));
        (imported.Fingerprint, imported.CrossSignedUntil).ShouldBe((rolled.Fingerprint, rolled.CrossSignedUntil));
        using var server = Start(standby, standbyKeys, DateTimeOffset.UtcNow);
        (server.CaFingerprint, server.PreviousCaFingerprint).ShouldBe((rolled.Fingerprint, old.CaFingerprint));
        await using var app = await ServeAsync(server);
        (await ConnectAsync(app, old.CaPem)).Trusted.ShouldBeTrue("the standby serves the devices that have not moved, as the primary does");
        (await ConnectAsync(app, server.CaPem!)).Trusted.ShouldBeTrue();

        // A CA exported before any rollover carries none: the standby then sends none, whatever was left there.
        var (fresh, freshKeys) = Machine("fresh");
        Snapshot(fresh, freshKeys, DateTimeOffset.UtcNow);
        var plain = LanCertificateManager.ExportCa(fresh, freshKeys, Password);
        LanCertificateManager.ImportCa(standby, standbyKeys, plain.Pkcs12, Password, DateTimeOffset.UtcNow).CrossSignedUntil.ShouldBeNull();
        using var again = Start(standby, standbyKeys, DateTimeOffset.UtcNow);
        (again.CaFingerprint, again.PreviousCaFingerprint).ShouldBe((plain.Fingerprint, null));
    }

    /// <summary>A new CA, for a key that may have been seen, ends any rollover: whoever holds the old key could sign a CA of their own.</summary>
    [Fact]
    public async Task A_new_CA_ends_the_rollover_and_a_device_that_pins_the_old_CA_must_be_set_up_again()
    {
        var (directories, keys) = Machine("primary");
        var old = Snapshot(directories, keys, Made);
        LanCertificateManager.RollCa(directories, keys, DateTimeOffset.UtcNow);

        // Within a second of the rollover, as a script may run them: each CA replaced is kept under a name of its own.
        LanCertificateManager.NewCa(directories, keys, "Nana Hospitals", DateTimeOffset.UtcNow);

        Directory.GetFiles(directories.Certificates, "hospital-ca.pfx.protected.replaced-*").Length.ShouldBe(2, "both CAs it replaced are kept");
        File.Exists(Path.Combine(directories.Certificates, "hospital-ca-cross.crt")).ShouldBeFalse("the rollover's certificates go with it");
        File.Exists(Path.Combine(directories.Certificates, "hc360-hospital-ca-previous.crt")).ShouldBeFalse();
        using var server = Start(directories, keys, DateTimeOffset.UtcNow);
        (server.PreviousCaFingerprint, server.CrossSignedUntil, server.PreviousCaPem).ShouldBe((null, null, null));
        server.SentWithCertificate.ShouldBeEmpty();
        await using var app = await ServeAsync(server);
        (await ConnectAsync(app, old.CaPem)).Trusted.ShouldBeFalse();
    }

    [Fact]
    public void A_rollover_is_refused_for_an_expired_CA_and_while_the_last_one_still_runs()
    {
        var (directories, keys) = Machine("primary");
        var now = DateTimeOffset.UtcNow;
        var old = Snapshot(directories, keys, Made);

        var expired = Should.Throw<InvalidOperationException>(() => LanCertificateManager.RollCa(directories, keys, old.CaExpiresAt.AddDays(1)));
        expired.Message.ShouldContain("an expired CA cannot vouch for the next one");
        expired.Message.ShouldContain("certificates new-ca");

        var rolled = LanCertificateManager.RollCa(directories, keys, now);
        var running = Should.Throw<InvalidOperationException>(() => LanCertificateManager.RollCa(directories, keys, now.AddDays(30)));
        running.Message.ShouldContain($"The last rollover runs until {rolled.CrossSignedUntil:dd MMM yyyy}");
        Snapshot(directories, keys, now).CaFingerprint.ShouldBe(rolled.Fingerprint, "nothing changed");

        // Once the old CA has expired, the new one can be rolled over in its turn.
        LanCertificateManager.RollCa(directories, keys, rolled.CrossSignedUntil.AddDays(1)).PreviousFingerprint.ShouldBe(rolled.Fingerprint);
    }

    [Fact]
    public void Once_the_old_CA_expires_the_server_stops_sending_the_certificate_it_signed()
    {
        var (directories, keys) = Machine("primary");
        var now = DateTimeOffset.UtcNow;
        Snapshot(directories, keys, Made);
        var rolled = LanCertificateManager.RollCa(directories, keys, now);
        var clock = new Clock(now);
        using var server = new LanCertificateManager(
            directories, new PlatformOptions { DataDirectory = directories.Root }, keys, NullLogger<LanCertificateManager>.Instance, clock);
        server.EnsureCertificates();
        using var newCa = X509Certificate2.CreateFromPem(server.CaPem!);
        using var cross = X509CertificateLoader.LoadCertificate(Crosses(server.SentWithCertificate, newCa).ShouldHaveSingleItem().RawData);

        // The running server's own check, the day after the old CA expired.
        clock.Now = rolled.CrossSignedUntil.AddDays(1);
        server.RefreshIfNeeded();

        (server.PreviousCaFingerprint, server.CrossSignedUntil).ShouldBe((null, null));
        server.SentWithCertificate.ShouldBeEmpty();
        server.ServerCertificateContext.ShouldNotBeNull();
        if (OperatingSystem.IsWindows())
        {
            WindowsTlsChain.Holds(cross).ShouldBeFalse("taken out of the store Windows' TLS sends from");
        }
        using var restarted = Start(directories, keys, clock.Now);
        restarted.PreviousCaFingerprint.ShouldBeNull("nor does it at its next start");
    }

    /// <summary>
    /// The outside probe that pins the old CA (CA-ROLLOVER.md) checks the chain the server sends against it: a warning that
    /// names the new CA, critical in the old CA's last 90 days. Pinned to the new CA it is well.
    /// </summary>
    [Fact]
    public async Task The_probe_that_pins_the_old_CA_warns_and_names_the_new_one_until_the_old_CA_ends()
    {
        var (directories, keys) = Machine("primary");
        var now = DateTimeOffset.UtcNow;
        var old = Snapshot(directories, keys, Made);
        var rolled = LanCertificateManager.RollCa(directories, keys, now);
        using var server = Start(directories, keys, now);
        await using var app = await ServeAsync(server);
        var address = new Uri(app.Urls.Single());

        var byOldPin = await CertificateProbe.ProbeAsync(address, old.CaFingerprint, null, now, Ct);
        var byNewPin = await CertificateProbe.ProbeAsync(address, rolled.Fingerprint, null, now, Ct);
        var lastMonths = await CertificateProbe.ProbeAsync(address, old.CaFingerprint, null, rolled.CrossSignedUntil.AddDays(-60), Ct);

        byOldPin.Outcome.ShouldBe(CertificateProbeOutcome.Warning, string.Join(" ", byOldPin.Findings));
        byOldPin.Findings.ShouldContain(f => f.Contains($"the server's CA is now {CertificateProbe.Group(rolled.Fingerprint)}", StringComparison.Ordinal)
            && f.Contains($"until {rolled.CrossSignedUntil:dd MMM yyyy}", StringComparison.Ordinal));
        byOldPin.Findings.ShouldNotContain(f => f.Contains("was not issued by the hospital CA", StringComparison.Ordinal));
        byNewPin.Outcome.ShouldBe(CertificateProbeOutcome.Ok, string.Join(" ", byNewPin.Findings));
        lastMonths.Outcome.ShouldBe(CertificateProbeOutcome.Critical, string.Join(" ", lastMonths.Findings));
        lastMonths.Findings.ShouldContain(f => f.Contains("The hospital CA was rolled over", StringComparison.Ordinal));
    }

    [Fact]
    public void After_a_rollover_the_server_advertises_the_CA_it_replaced_as_well_as_its_own()
    {
        var (directories, keys) = Machine("primary");
        var old = Snapshot(directories, keys, Made);
        LanCertificateManager.RollCa(directories, keys, DateTimeOffset.UtcNow);
        using var server = Start(directories, keys, DateTimeOffset.UtcNow);
        var options = new PlatformOptions();

        var txt = MdnsAdvertiser.Profile(options.Network, options.HttpsPort, "HC360-SRV", "1.4.2", "NH", server.CaFingerprint, [IPAddress.Parse("192.168.1.10")],
            server.PreviousCaFingerprint).Resources.OfType<TXTRecord>().Single();

        ServerAdvertisement.Read(txt.Strings).ShouldBe(new AdvertisedProperties("1.4.2", "NH", server.CaFingerprint, "HC360-SRV", old.CaFingerprint));
        txt.Strings.ShouldContain($"fpprev={old.CaFingerprint}");
    }

    /// <summary>The certificates among <paramref name="certificates"/> for <paramref name="ca"/>'s key that are not the CA itself.</summary>
    private static List<X509Certificate2> Crosses(IEnumerable<X509Certificate2> certificates, X509Certificate2 ca) =>
    [
        .. certificates.Where(c => !c.RawData.AsSpan().SequenceEqual(ca.RawData)
            && c.PublicKey.EncodedKeyValue.RawData.AsSpan().SequenceEqual(ca.PublicKey.EncodedKeyValue.RawData)),
    ];

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

    private static Started Snapshot((DataDirectories Directories, IDataProtectionProvider Keys) machine, DateTimeOffset now) =>
        Snapshot(machine.Directories, machine.Keys, now);

    /// <summary>The CA a server started with at <paramref name="now"/>, as the devices pinned it.</summary>
    private static Started Snapshot(DataDirectories directories, IDataProtectionProvider keys, DateTimeOffset now)
    {
        using var certificates = Start(directories, keys, now);
        return new Started(certificates.CaFingerprint!, certificates.CaPem!, certificates.CaExpiresAt!.Value);
    }

    private sealed record Started(string CaFingerprint, string CaPem, DateTimeOffset CaExpiresAt);

    /// <summary>
    /// A real HTTPS listener with HC360's TLS (<see cref="HospitalHttps"/>), handing out the CA and, after a rollover, the one it
    /// replaced, where the devices and the probe read them. <paramref name="leafAlone"/> serves the certificate alone, as Kestrel
    /// served it before HC360 sent a chain.
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
        app.MapGet("/" + CertificateProbe.PreviousCaCertificatePath, () =>
            certificates.PreviousCaPem is { } pem ? Results.Text(pem, "application/x-x509-ca-cert") : Results.NotFound());
        await app.StartAsync(Ct);
        return app;
    }

    /// <summary>
    /// A TLS connection from a device that pinned <paramref name="pinnedPem"/>, checking the server as HC360 Desktop and the lab
    /// agent do: the certificate names the address, is for a TLS server, and chains to exactly the pinned CA through what the
    /// server sent. Says whether the device trusted it, and what the server sent after its own certificate.
    /// </summary>
    private static async Task<Connection> ConnectAsync(WebApplication app, string pinnedPem)
    {
        var address = new Uri(app.Urls.Single());
        using var client = new TcpClient();
        await client.ConnectAsync(address.Host, address.Port, Ct);
        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        using var pinned = X509Certificate2.CreateFromPem(pinnedPem);
        var trusted = false;
        var sent = new List<X509Certificate2>();
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = address.Host,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, certificate, handshake, _) =>
                {
                    using var served = X509CertificateLoader.LoadCertificate(certificate!.GetRawCertData());
                    foreach (var other in handshake?.ChainPolicy.ExtraStore ?? [])
                    {
                        if (!other.RawData.AsSpan().SequenceEqual(served.RawData))
                        {
                            sent.Add(X509CertificateLoader.LoadCertificate(other.RawData));
                        }
                    }

                    trusted = TrustedBy(pinned, served, handshake, address.Host);
                    return trusted;
                },
            }, Ct);
        }
        catch (AuthenticationException)
        {
            // Refused by the device: trusted stays false.
        }

        return new Connection(trusted, sent);
    }

    private sealed record Connection(bool Trusted, IReadOnlyList<X509Certificate2> Sent);

    /// <summary>HC360 Desktop's and the lab agent's check of the server (ServerTrust.Validate), line for line.</summary>
    private static bool TrustedBy(X509Certificate2 pinned, X509Certificate2 served, X509Chain? handshake, string host)
    {
        if (!served.MatchesHostname(host, allowWildcards: false, allowCommonName: false))
        {
            return false;
        }

        var sent = new X509Certificate2Collection();
        foreach (var other in handshake?.ChainPolicy.ExtraStore ?? [])
        {
            if (!other.RawData.AsSpan().SequenceEqual(served.RawData))
            {
                sent.Add(other);
            }
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(pinned);
        chain.ChainPolicy.ExtraStore.AddRange(sent);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = DateTime.Now;
        if (!chain.Build(served))
        {
            return false;
        }

        for (var link = 1; link < chain.ChainElements.Count - 1; link++)
        {
            var linked = chain.ChainElements[link].Certificate.RawData;
            if (!sent.Any(certificate => certificate.RawData.AsSpan().SequenceEqual(linked)))
            {
                return false;
            }
        }

        return chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(pinned.RawData);
    }

    /// <summary>A clock the test sets; the server's own daily check is called by the test rather than waited for.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
