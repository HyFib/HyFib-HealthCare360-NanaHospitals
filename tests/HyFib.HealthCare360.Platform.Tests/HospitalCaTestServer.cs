using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A Server Node for the hospital CA rollover tests (docs/deployment/CA-ROLLOVER.md): its data directory and keys, its TLS
/// as HC360 starts it at a given time (<see cref="LanCertificateManager"/>), and a real HTTPS listener with HC360's TLS
/// (<see cref="HospitalHttps"/>) and the routes the server hands its CA certificates out at
/// (<see cref="HospitalCaEndpoints"/>). Devices connect to it as HC360 Desktop and the lab agent do: pinned to
/// the CAs they hold (<see cref="HospitalCaPins"/>).
/// </summary>
/// <param name="keys">The machine's keys: another node's, for a copy of its data directory (a cloned disk); its own otherwise.</param>
internal sealed class HospitalCaTestServer(string root, string name, IDataProtectionProvider? keys = null)
{
    public DataDirectories Directories { get; } = new(new PlatformOptions { DataDirectory = Path.Combine(root, name) });

    public IDataProtectionProvider Keys { get; } = keys ?? new EphemeralDataProtectionProvider();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The server's TLS as HC360 starts it at <paramref name="now"/>.</summary>
    public LanCertificateManager Start(DateTimeOffset now)
    {
        var certificates = new LanCertificateManager(
            Directories, new PlatformOptions { DataDirectory = Directories.Root }, Keys, NullLogger<LanCertificateManager>.Instance, new RolloverClock(now));
        certificates.EnsureCertificates();
        return certificates;
    }

    /// <summary>The CA the server started with at <paramref name="now"/>, as the devices pinned it.</summary>
    public PinnedCa Ca(DateTimeOffset now)
    {
        using var certificates = Start(now);
        return new PinnedCa(certificates.CaFingerprint!, certificates.CaPem!, certificates.CaExpiresAt!.Value);
    }

    /// <summary>
    /// A copy of this server's data directory, as another node with the same keys: a standby as it was before what this
    /// server does next.
    /// </summary>
    public HospitalCaTestServer CopyAs(string root, string name)
    {
        var copy = new HospitalCaTestServer(root, name, Keys);
        foreach (var file in Directory.GetFiles(Directories.Root, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy.Directories.Root, Path.GetRelativePath(Directories.Root, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return copy;
    }

    /// <summary>A real HTTPS listener with HC360's TLS, handing out the CA certificates where the server does.</summary>
    public static async Task<WebApplication> ServeAsync(LanCertificateManager certificates)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(certificates);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https => HospitalHttps.Configure(https, certificates))));
        var app = builder.Build();
        HospitalCaEndpoints.Map(app.MapGroup("/api/v1/system"));
        await app.StartAsync(Ct);
        return app;
    }

    /// <summary>
    /// An HTTP client whose connections are pinned to <paramref name="pins"/>, as HC360 Desktop's and the lab agent's are
    /// (their ServerTrust.CreateHttpClient): the server's certificate is checked with <see cref="HospitalCaPins.Validate"/>.
    /// </summary>
    public static HttpClient PinnedClient(WebApplication app, HospitalCaPins pins)
    {
        var address = new Uri(app.Urls.Single());
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, chain, _) => pins.Validate(certificate, chain, address.Host) },
        };
        return new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// A TLS connection from a device that holds <paramref name="pins"/>: whether it trusted the server, and what the server
    /// sent besides its own certificate.
    /// </summary>
    public static async Task<Connection> ConnectAsync(WebApplication app, HospitalCaPins pins)
    {
        var address = new Uri(app.Urls.Single());
        using var client = new TcpClient();
        await client.ConnectAsync(address.Host, address.Port, Ct);
        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
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

                    trusted = pins.Validate(certificate, handshake, address.Host);
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

    /// <summary>Whether a device that holds <paramref name="pins"/> trusts the server: a TLS connection, checked as the apps check it.</summary>
    public static async Task<bool> TrustsAsync(WebApplication app, HospitalCaPins pins) => (await ConnectAsync(app, pins)).Trusted;
}

/// <summary>A CA a server started with, as the devices pinned it.</summary>
internal sealed record PinnedCa(string Fingerprint, string Pem, DateTimeOffset ExpiresAt);

/// <summary>A TLS connection's outcome: whether the device trusted the server, and what the server sent besides its own certificate.</summary>
internal sealed record Connection(bool Trusted, IReadOnlyList<X509Certificate2> Sent);

/// <summary>A clock that says <see cref="Now"/>, which a test may move; the server's own checks are called rather than waited for.</summary>
internal sealed class RolloverClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
