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
/// After the hospital CA is rolled over ('certificates roll-ca'), HC360 Desktop takes the new CA by itself on the word of the
/// CA it pins, keeps the old one as a second pin, and an IT policy that names the old CA keeps accepting the new one
/// (docs/deployment/CA-ROLLOVER.md). The rollover is made here as the server makes it: the new CA has the old one's name and
/// a new key, and the old CA signs a certificate for the new key, which the server sends with its own.
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
    public async Task Takes_the_new_CA_over_its_pinned_connection_and_keeps_the_old_one_as_a_second_pin()
    {
        using var rollover = Rollover.Make();
        using var served = ServerTrustTests.UsableForTls(Leaf(rollover.New, "localhost"));

        // As the server does after a rollover: Windows' TLS sends only what it finds in this computer's stores.
        WindowsTlsChain.Publish([rollover.Cross, rollover.Old]).ShouldBeTrue();
        HospitalCaSuccessor? successor;
        var oldPem = rollover.Old.ExportCertificatePem();
        using var trust = new ServerTrust(oldPem);
        try
        {
            var sent = SslStreamCertificateContext.Create(served, [rollover.Cross, rollover.Old], offline: true);
            var (address, serving) = ServeCaCertificate(sent, rollover.New.ExportCertificatePem());
            using (var client = new ServerClient(address, trust))
            {
                successor = await client.FindCaSuccessorAsync(Ct);
            }

            await serving;
        }
        finally
        {
            WindowsTlsChain.Withdraw([rollover.Cross, rollover.Old]);
        }

        successor.ShouldNotBeNull("the old CA vouched for the new one in the handshake").Fingerprint.ShouldBe(ServerTrust.ComputeFingerprint(rollover.New));

        var settings = new DesktopSettings { ServerUrl = "https://localhost:7360/", CaCertificatePem = oldPem, CaFingerprint = trust.Fingerprint };
        settings.Follow(successor);
        trust.Pins.Follow(successor);

        (settings.CaFingerprint, settings.PreviousCaCertificatePem, settings.CaCrossCertificatePem).ShouldBe((successor.Fingerprint, oldPem, successor.CrossPem));
        trust.Fingerprint.ShouldBe(successor.Fingerprint);
        using var stillOnTheOldCa = Leaf(rollover.Old, "HC360-SRV");
        trust.IsTrusted(stillOnTheOldCa, "HC360-SRV").ShouldBeTrue("a server not given the rollover yet: the old CA is a second pin");
        using var onlyTheNew = new ServerTrust(successor.CaPem);
        onlyTheNew.IsTrusted(stillOnTheOldCa, "HC360-SRV").ShouldBeFalse();
    }

    [Fact]
    public void An_IT_policy_that_names_the_old_CA_keeps_a_workstation_that_followed_its_rollover_and_no_other()
    {
        using var rollover = Rollover.Make();
        using var another = Rollover.Make();
        var oldFingerprint = ServerTrust.ComputeFingerprint(rollover.Old);
        var newFingerprint = ServerTrust.ComputeFingerprint(rollover.New);
        var followed = Saved(rollover.New, rollover.Old, rollover.Cross);

        Load(followed, oldFingerprint).CaCertificatePem.ShouldNotBeNull("the policy's CA vouched for the one it pins");
        Load(followed, newFingerprint).CaCertificatePem.ShouldNotBeNull("IT updated the policy to the new CA");
        Load(Saved(rollover.New, rollover.Old, another.Cross), oldFingerprint).CaCertificatePem.ShouldBeNull("another rollover's word");
        Load(Saved(rollover.New, rollover.Old, cross: null), oldFingerprint).CaCertificatePem.ShouldBeNull("no word at all");
        Load(Saved(another.New, rollover.Old, rollover.Cross), oldFingerprint).CaCertificatePem.ShouldBeNull("a CA the policy's CA did not vouch for");

        // The file is the user's to edit: the certificate it pins decides, not the fingerprint written beside it.
        var edited = Saved(another.Old, null, null) with { CaFingerprint = oldFingerprint };
        Load(edited, oldFingerprint).CaCertificatePem.ShouldBeNull();
        Load(Saved(rollover.Old, null, null), oldFingerprint).CaCertificatePem.ShouldNotBeNull("as before any rollover");
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

    private static SavedSettings Saved(X509Certificate2 ca, X509Certificate2? previous, X509Certificate2? cross) =>
        new("https://hc360-srv:7360/", ca.ExportCertificatePem(), ServerTrust.ComputeFingerprint(ca), previous?.ExportCertificatePem(), cross?.ExportCertificatePem());

    private sealed record SavedSettings(string ServerUrl, string CaCertificatePem, string CaFingerprint, string? PreviousCaCertificatePem, string? CaCrossCertificatePem);

    /// <summary>
    /// An HTTPS server on this machine for one request, as HC360's Kestrel serves the CA certificate: it sends
    /// <paramref name="sent"/> in the handshake and answers with <paramref name="caPem"/>.
    /// </summary>
    private static (Uri Address, Task Serving) ServeCaCertificate(SslStreamCertificateContext sent, string caPem)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            try
            {
                using var accepted = await listener.AcceptTcpClientAsync(Ct);
                await using var tls = new SslStream(accepted.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = sent }, Ct);
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

                var body = Encoding.ASCII.GetBytes(caPem);
                await tls.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/x-x509-ca-cert\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), Ct);
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
    /// A rollover as 'certificates roll-ca' makes it: the old CA, the new one in its name with a new key, and the certificate
    /// the old CA signed for the new key.
    /// </summary>
    private sealed class Rollover : IDisposable
    {
        private Rollover(X509Certificate2 old, X509Certificate2 @new, X509Certificate2 cross)
        {
            Old = old;
            New = @new;
            Cross = cross;
        }

        public X509Certificate2 Old { get; }

        public X509Certificate2 New { get; }

        public X509Certificate2 Cross { get; }

        public static Rollover Make()
        {
            using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var old = Ca(oldKey, issuer: null);
            var @new = Ca(newKey, issuer: null);
            var cross = Ca(newKey, issuer: old);
            return new Rollover(old, @new, cross);
        }

        public void Dispose()
        {
            Old.Dispose();
            New.Dispose();
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
