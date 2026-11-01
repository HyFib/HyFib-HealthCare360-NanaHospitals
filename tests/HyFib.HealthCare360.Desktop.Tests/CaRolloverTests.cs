using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Desktop.Services;
using HyFib.HealthCare360.WindowsInterop.Trust;

namespace HyFib.HealthCare360.Desktop.Tests;

/// <summary>
/// HC360 Desktop follows the hospital CA's two-step rollover by itself (docs/deployment/CA-ROLLOVER.md): once IT prepared the
/// next CA it holds it on the word of the CA it pins, once the server switched to it it pins it and keeps the old one as a
/// second pin, and each step is saved, or not taken at all. An IT policy that names the old CA keeps accepting the new one;
/// one that names the next CA has the workstation pin that alone. The rollover is made here as the server makes it: the next
/// CA has the current one's name and a new key, and the current CA signs a certificate for the new key, which the server
/// hands out with the next CA. Over Windows' TLS, which sends the server's certificate alone.
/// </summary>
public sealed class CaRolloverTests : IDisposable
{
    private const string CaName = "CN=Nana Hospitals HC360 CA";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-rollover-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Holds_the_next_CA_over_its_pinned_connection_then_switches_to_it_and_saves_each_step()
    {
        using var rollover = Rollover.Make();
        var (oldPem, nextPem, crossPem) = (rollover.Old.ExportCertificatePem(), rollover.Next.ExportCertificatePem(), rollover.Cross.ExportCertificatePem());
        var nextFingerprint = ServerTrust.ComputeFingerprint(rollover.Next);
        using var trust = new ServerTrust(oldPem);
        var settings = new DesktopSettings { ServerUrl = "https://localhost:7360/", CaCertificatePem = oldPem, CaFingerprint = trust.Fingerprint };
        var saves = 0;

        // Prepared: the server still serves with the current CA, and hands out the next one with its word for it.
        using (var servedByOld = ServerTrustTests.UsableForTls(Leaf(rollover.Old, "localhost")))
        {
            var (address, serving) = ServeOnce(servedByOld, path => path == "/" + HospitalCaFollow.NextCaPath ? $"{nextPem}\n{crossPem}\n" : null);
            HospitalCaStep? step;
            using (var client = new ServerClient(address, trust))
            {
                step = await client.CheckCaAsync(Ct);
            }

            await serving;
            var hold = step.ShouldBeOfType<HospitalCaStep.HoldNext>();
            hold.Next.Fingerprint.ShouldBe(nextFingerprint);
            settings.Take(hold, () => saves++);
            trust.Pins.Hold(hold.Next);
        }

        (settings.CaCertificatePem, settings.NextCaCertificatePem, settings.NextCaCrossCertificatePem).ShouldBe((oldPem, nextPem, crossPem));
        saves.ShouldBe(1);

        // Switched: the server serves with the next CA, which the workstation holds, and says it is its CA.
        using (var servedByNext = ServerTrustTests.UsableForTls(Leaf(rollover.Next, "localhost")))
        {
            var (address, serving) = ServeOnce(servedByNext, path => path == "/" + HospitalCaFollow.CaPath ? nextPem : null);
            HospitalCaStep? step;
            using (var client = new ServerClient(address, trust))
            {
                step = await client.CheckCaAsync(Ct);
            }

            await serving;
            settings.Take(step.ShouldBeOfType<HospitalCaStep.SwitchToNext>(), () => saves++);
            trust.Pins.SwitchToNext();
        }

        (settings.CaCertificatePem, settings.CaFingerprint, settings.PreviousCaCertificatePem, settings.CaCrossCertificatePem)
            .ShouldBe((nextPem, nextFingerprint, oldPem, crossPem));
        (settings.NextCaCertificatePem, settings.NextCaCrossCertificatePem).ShouldBe((null, null));
        saves.ShouldBe(2);
        trust.Fingerprint.ShouldBe(nextFingerprint);
        using var stillOnTheOldCa = Leaf(rollover.Old, "HC360-SRV");
        trust.IsTrusted(stillOnTheOldCa, "HC360-SRV").ShouldBeTrue("a standby not switched yet: the old CA is a second pin");
        using var onlyTheNext = new ServerTrust(nextPem);
        onlyTheNext.IsTrusted(stillOnTheOldCa, "HC360-SRV").ShouldBeFalse();
    }

    [Fact]
    public void A_step_that_cannot_be_saved_leaves_the_settings_as_they_were()
    {
        using var rollover = Rollover.Make();
        var (oldPem, nextPem, crossPem) = (rollover.Old.ExportCertificatePem(), rollover.Next.ExportCertificatePem(), rollover.Cross.ExportCertificatePem());
        var settings = new DesktopSettings { ServerUrl = "https://localhost:7360/", CaCertificatePem = oldPem, CaFingerprint = ServerTrust.ComputeFingerprint(rollover.Old) };
        var hold = new HospitalCaStep.HoldNext(new HospitalCaNext(nextPem, ServerTrust.ComputeFingerprint(rollover.Next), crossPem));

        Should.Throw<IOException>(() => settings.Take(hold, () => throw new IOException("disk full")));
        settings.NextCaCertificatePem.ShouldBeNull("not held: the next check holds it again");

        settings.Take(hold, () => { });
        Should.Throw<UnauthorizedAccessException>(() => settings.Take(new HospitalCaStep.SwitchToNext(hold.Next.Fingerprint), () => throw new UnauthorizedAccessException()));
        (settings.CaCertificatePem, settings.NextCaCertificatePem, settings.PreviousCaCertificatePem).ShouldBe((oldPem, nextPem, null), "not switched");
    }

    [Fact]
    public void An_IT_policy_that_names_the_old_CA_keeps_a_workstation_that_switched_and_no_other()
    {
        using var rollover = Rollover.Make();
        using var another = Rollover.Make();
        var oldFingerprint = ServerTrust.ComputeFingerprint(rollover.Old);
        var nextFingerprint = ServerTrust.ComputeFingerprint(rollover.Next);
        var switched = Saved(rollover.Next, previous: rollover.Old, cross: rollover.Cross);

        Load(switched, oldFingerprint).CaCertificatePem.ShouldNotBeNull("the policy's CA vouched for the one it pins");
        Load(switched, nextFingerprint).CaCertificatePem.ShouldNotBeNull("IT updated the policy to the new CA");
        Load(Saved(rollover.Next, previous: rollover.Old, cross: another.Cross), oldFingerprint).CaCertificatePem.ShouldBeNull("another rollover's word");
        Load(Saved(rollover.Next, previous: rollover.Old, cross: null), oldFingerprint).CaCertificatePem.ShouldBeNull("no word at all");
        Load(Saved(another.Next, previous: rollover.Old, cross: rollover.Cross), oldFingerprint).CaCertificatePem.ShouldBeNull("a CA the policy's CA did not vouch for");

        // The file is the user's to edit: the certificate it pins decides, not the fingerprint written beside it.
        var edited = Saved(another.Old) with { CaFingerprint = oldFingerprint };
        Load(edited, oldFingerprint).CaCertificatePem.ShouldBeNull();
        Load(Saved(rollover.Old), oldFingerprint).CaCertificatePem.ShouldNotBeNull("as before any rollover");
    }

    /// <summary>
    /// IT may name the next CA in the policy before the workstation switched: it then pins that CA alone, from the policy's
    /// own fingerprint. The CA it pinned until then goes: nothing the policy names vouches for it, and the user's file could
    /// hold any CA there, with a word for the next CA signed by that CA's own key.
    /// </summary>
    [Fact]
    public void An_IT_policy_that_names_the_next_CA_has_the_workstation_pin_it_alone()
    {
        using var rollover = Rollover.Make();
        using var forged = Rollover.Make();
        var nextFingerprint = ServerTrust.ComputeFingerprint(rollover.Next);
        var nextPem = rollover.Next.ExportCertificatePem();
        var holding = Saved(rollover.Old, next: rollover.Next, nextCross: rollover.Cross);

        var renamed = Load(holding, nextFingerprint);
        (renamed.CaCertificatePem, renamed.CaFingerprint).ShouldBe((nextPem, nextFingerprint));
        (renamed.NextCaCertificatePem, renamed.PreviousCaCertificatePem, renamed.CaCrossCertificatePem).ShouldBe((null, null, null));
        Load(holding, ServerTrust.ComputeFingerprint(rollover.Old)).NextCaCertificatePem.ShouldBe(nextPem, "a policy naming the CA it pins keeps the next CA it holds");

        // Another CA pinned in the file, with its own word for the hospital's next CA: the workstation pins the next CA alone.
        using var vouchOfItsOwn = Rollover.Vouch(forged.Old, rollover.Next);
        var tampered = Load(Saved(forged.Old, next: rollover.Next, nextCross: vouchOfItsOwn), nextFingerprint);
        (tampered.CaCertificatePem, tampered.PreviousCaCertificatePem).ShouldBe((nextPem, null));
        Load(holding, ServerTrust.ComputeFingerprint(forged.Next)).CaCertificatePem.ShouldBeNull("a CA it does not hold: set up again");
    }

    /// <summary>The user's settings file as HC360 Desktop writes it, read with the machine policy naming <paramref name="policyFingerprint"/>.</summary>
    private DesktopSettings Load(SavedSettings saved, string policyFingerprint)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var settingsPath = Path.Combine(folder, "settings.json");
        var policyPath = Path.Combine(folder, "policy.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(saved, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        File.WriteAllText(policyPath, JsonSerializer.Serialize(new { caFingerprint = policyFingerprint }));
        return DesktopSettings.Load(settingsPath, policyPath);
    }

    private static SavedSettings Saved(
        X509Certificate2 ca, X509Certificate2? previous = null, X509Certificate2? cross = null, X509Certificate2? next = null, X509Certificate2? nextCross = null) =>
        new("https://hc360-srv:7360/", ca.ExportCertificatePem(), ServerTrust.ComputeFingerprint(ca), previous?.ExportCertificatePem(), cross?.ExportCertificatePem(),
            next?.ExportCertificatePem(), nextCross?.ExportCertificatePem());

    private sealed record SavedSettings(
        string ServerUrl, string CaCertificatePem, string CaFingerprint, string? PreviousCaCertificatePem, string? CaCrossCertificatePem,
        string? NextCaCertificatePem, string? NextCaCrossCertificatePem);

    /// <summary>
    /// An HTTPS server on this machine for one request, as HC360's Kestrel serves the CA certificates: Windows' TLS sends
    /// <paramref name="served"/> alone, and the answer to the path asked is <paramref name="answer"/>'s, 404 when it has none.
    /// </summary>
    private static (Uri Address, Task Serving) ServeOnce(X509Certificate2 served, Func<string, string?> answer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var context = SslStreamCertificateContext.Create(served, null, offline: true);
        var serving = Task.Run(async () =>
        {
            try
            {
                using var accepted = await listener.AcceptTcpClientAsync(Ct);
                await using var tls = new SslStream(accepted.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context }, Ct);
                var request = new StringBuilder();
                var buffer = new byte[1024];
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await tls.ReadAsync(buffer, Ct);
                    if (read == 0)
                    {
                        return;
                    }

                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var path = request.ToString().Split(' ')[1];
                var body = answer(path) is { } text ? Encoding.ASCII.GetBytes(text) : [];
                var status = body.Length > 0 ? "200 OK" : "404 Not Found";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: application/x-pem-file\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), Ct);
                await tls.WriteAsync(body, Ct);
                await tls.FlushAsync(Ct);
            }
            finally
            {
                listener.Dispose();
            }
        }, Ct);
        return (new Uri($"https://localhost:{port}/"), serving);
    }

    /// <summary>A server certificate for <paramref name="host"/> (and 127.0.0.1) issued by <paramref name="ca"/>.</summary>
    private static X509Certificate2 Leaf(X509Certificate2 ca, string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(1), RandomNumberGenerator.GetBytes(12));
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>
    /// A rollover as 'certificates prepare-ca' makes it: the current CA, the next one in its name with a new key, and the
    /// certificate the current CA signed for the next key.
    /// </summary>
    private sealed class Rollover : IDisposable
    {
        private Rollover(X509Certificate2 old, X509Certificate2 next, X509Certificate2 cross)
        {
            Old = old;
            Next = next;
            Cross = cross;
        }

        public X509Certificate2 Old { get; }

        public X509Certificate2 Next { get; }

        public X509Certificate2 Cross { get; }

        public static Rollover Make()
        {
            using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var old = Ca(oldKey, issuer: null);
            var next = Ca(nextKey, issuer: null);
            var cross = Ca(nextKey, issuer: old);
            return new Rollover(old, next, cross);
        }

        /// <summary>A certificate <paramref name="issuer"/> signs, in <paramref name="ca"/>'s name, for its key.</summary>
        public static X509Certificate2 Vouch(X509Certificate2 issuer, X509Certificate2 ca)
        {
            var request = new CertificateRequest(ca.SubjectName, ca.PublicKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
            return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), issuer.NotAfter, RandomNumberGenerator.GetBytes(12));
        }

        public void Dispose()
        {
            Old.Dispose();
            Next.Dispose();
            Cross.Dispose();
        }

        private static X509Certificate2 Ca(ECDsa key, X509Certificate2? issuer)
        {
            var request = new CertificateRequest(CaName, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            if (issuer is null)
            {
                return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            }

            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
            return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), issuer.NotAfter, RandomNumberGenerator.GetBytes(12));
        }
    }
}
