using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
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

        (await HandshakeAsync(SslStreamCertificateContext.Create(server.ServerCertificate, null, offline: true), enrolledBefore)).ShouldBeFalse();
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
