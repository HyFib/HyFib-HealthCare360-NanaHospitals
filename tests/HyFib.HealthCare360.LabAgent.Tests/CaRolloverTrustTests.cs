using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Trust;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.LabAgent.Tests;

/// <summary>
/// After the hospital CA is rolled over ('certificates roll-ca'), an agent enrolled before it, which pins the old CA, keeps
/// trusting the server, and one enrolled after it, which pins the new CA, trusts it too: the server's own code makes the CA,
/// rolls it over and builds what it sends, and the agent's own check (<see cref="ServerTrust"/>) judges it over TLS. Run in
/// the Windows CI job, where Windows' TLS sends the chain and builds it.
/// </summary>
public sealed class CaRolloverTrustTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc360-agent-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task An_agent_that_pinned_the_old_CA_and_one_that_pinned_the_new_CA_both_trust_the_rolled_server()
    {
        var (directories, keys) = Machine("server");
        string oldCa;
        using (var first = Start(directories, keys))
        {
            oldCa = first.CaPem!;
        }

        LanCertificateManager.RollCa(directories, keys, DateTimeOffset.UtcNow);
        using var server = Start(directories, keys);
        var (otherDirectories, otherKeys) = Machine("other-hospital");
        using var otherHospital = Start(otherDirectories, otherKeys);

        using var enrolledBefore = new ServerTrust(oldCa);
        using var enrolledAfter = new ServerTrust(server.CaPem!);
        using var stranger = new ServerTrust(otherHospital.CaPem!);

        (await HandshakeAsync(server.ServerCertificateContext!, enrolledBefore)).ShouldBeTrue("the agent enrolled before the rollover keeps working");
        (await HandshakeAsync(server.ServerCertificateContext!, enrolledAfter)).ShouldBeTrue();
        (await HandshakeAsync(server.ServerCertificateContext!, stranger)).ShouldBeFalse();
    }

    /// <summary>Served without the certificate the old CA signed, as before, the agent that pinned the old CA refuses the server.</summary>
    [Fact]
    public async Task Without_the_certificate_the_old_CA_signed_the_agent_that_pinned_it_refuses_the_rolled_server()
    {
        var (directories, keys) = Machine("server");
        string oldCa;
        using (var first = Start(directories, keys))
        {
            oldCa = first.CaPem!;
        }

        LanCertificateManager.RollCa(directories, keys, DateTimeOffset.UtcNow);
        using var server = Start(directories, keys);
        using var enrolledBefore = new ServerTrust(oldCa);

        // Windows' TLS sends what it finds in this computer's stores, where HC360 put the certificate: out of them, so the
        // server certificate goes out alone, as it did before.
        WindowsTlsChain.Withdraw(server.SentWithCertificate);
        (await HandshakeAsync(SslStreamCertificateContext.Create(server.ServerCertificate, null, offline: true), enrolledBefore)).ShouldBeFalse();
    }

    /// <summary>
    /// The agent takes the new CA by itself: over its connection pinned to the old CA it asks the server for its CA and keeps
    /// what the server sent, which holds the certificate the old CA signed for the new key (<see cref="ServerApi.FindCaSuccessorAsync"/>).
    /// It saves the new CA with the old one as a second pin, says both in its heartbeat, and keeps trusting the server once the
    /// old CA has expired and the server no longer sends that certificate.
    /// </summary>
    [Fact]
    public async Task An_agent_that_pinned_the_old_CA_takes_the_new_one_over_its_pinned_connection_and_keeps_the_old_as_a_second_pin()
    {
        var (directories, keys) = Machine("server");
        string oldCa;
        using (var first = Start(directories, keys))
        {
            oldCa = first.CaPem!;
        }

        LanCertificateManager.RollCa(directories, keys, DateTimeOffset.UtcNow);
        using var server = Start(directories, keys);
        using var trust = new ServerTrust(oldCa);
        var (address, serving) = ServeCaCertificate(server.ServerCertificateContext!, server.CaPem!);
        HospitalCaSuccessor successor;
        using (var api = new ServerApi(address, trust))
        {
            successor = (await api.FindCaSuccessorAsync(DateTimeOffset.UtcNow, Ct)).ShouldNotBeNull("the old CA vouched for the new one");
        }

        await serving;
        successor.Fingerprint.ShouldBe(server.CaFingerprint);

        var path = Path.Combine(_folder, "agent.json");
        var configuration = new AgentConfiguration
        {
            ServerUrl = address.ToString(),
            CaCertificatePem = oldCa,
            CaFingerprint = trust.Fingerprint,
            DeviceId = Guid.CreateVersion7(),
            DeviceSecret = "s3cr3t-device-key",
        };
        configuration.Follow(successor);
        configuration.Save(path);
        trust.Pins.Follow(successor);

        var saved = AgentConfiguration.Load(path);
        saved.CaFingerprint.ShouldBe(server.CaFingerprint);
        saved.PreviousCaCertificatePem.ShouldBe(oldCa, "the CA it pinned until now, a second pin");
        saved.IsEnrolled.ShouldBeTrue();
        using (var replaced = X509Certificate2.CreateFromPem(oldCa))
        {
            trust.Pins.Fingerprints(DateTimeOffset.UtcNow).ShouldBe([server.CaFingerprint!, ServerTrust.ComputeFingerprint(replaced)], "its heartbeat says both, the one it pins first");
        }

        using var restarted = new ServerTrust(saved.CaCertificatePem!, saved.PreviousCaCertificatePem);
        (await HandshakeAsync(server.ServerCertificateContext!, restarted)).ShouldBeTrue();

        // Once the old CA expired, the server sends no certificate it signed (out of the store Windows' TLS sends from).
        WindowsTlsChain.Withdraw(server.SentWithCertificate);
        (await HandshakeAsync(SslStreamCertificateContext.Create(server.ServerCertificate, null, offline: true), restarted)).ShouldBeTrue();
    }

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
                var head = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: application/x-x509-ca-cert\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await tls.WriteAsync(head, Ct);
                await tls.WriteAsync(body, Ct);
                await tls.FlushAsync(Ct);
            }
            finally
            {
                listener.Dispose();
            }
        }, Ct);
        return (new Uri($"https://127.0.0.1:{port}/"), serving);
    }

    private (DataDirectories Directories, IDataProtectionProvider Keys) Machine(string name) =>
        (new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_folder, name) }), new EphemeralDataProtectionProvider());

    private static LanCertificateManager Start(DataDirectories directories, IDataProtectionProvider keys)
    {
        var certificates = new LanCertificateManager(
            directories, new PlatformOptions { DataDirectory = directories.Root }, keys, NullLogger<LanCertificateManager>.Instance);
        certificates.EnsureCertificates();
        return certificates;
    }

    /// <summary>
    /// A TLS connection on this machine: the server sends <paramref name="sent"/>, as HC360's Kestrel does, and the agent checks it
    /// with <paramref name="trust"/> for 127.0.0.1, as its calls to the server do. Whether the agent trusted the server.
    /// </summary>
    private static async Task<bool> HandshakeAsync(SslStreamCertificateContext sent, ServerTrust trust)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(Ct);
            await using var tls = new SslStream(accepted.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = sent }, Ct);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                // The agent refused the server: the handshake ends here.
            }
        }, Ct);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        await using var agent = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, chain, _) => trust.Validate(certificate, chain, "127.0.0.1"));
        bool trusted;
        try
        {
            await agent.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, Ct);
            trusted = true;
        }
        catch (AuthenticationException)
        {
            trusted = false;
        }

        await serving;
        return trusted;
    }
}
