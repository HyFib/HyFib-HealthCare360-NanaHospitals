using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyFib.HealthCare360.Desktop.Services;

namespace HyFib.HealthCare360.Desktop.Tests;

public sealed class ServerFallbackTests
{
    private static readonly string Fingerprint = new('A', 64);

    [Fact]
    public void Remembers_the_newest_address_first_and_each_once()
    {
        ServerFallback.Remember(["https://hc360-srv:7360/", "https://192.168.1.10:7360/"], new Uri("https://192.168.1.10:7360/patients"))
            .ShouldBe(["https://192.168.1.10:7360/", "https://hc360-srv:7360/"]);

        ServerFallback.Remember(Enumerable.Range(1, 20).Select(i => $"https://10.0.0.{i}:7360/"), new Uri("https://hc360-srv:7360/"))
            .Count.ShouldBe(ServerFallback.AddressesKept);
    }

    [Fact]
    public void The_settings_change_only_when_the_address_was_not_already_first()
    {
        var settings = new DesktopSettings { ServerAddresses = ["https://hc360-srv:7360/", "https://192.168.1.10:7360/"] };

        settings.RememberServer(new Uri("https://HC360-SRV:7360/")).ShouldBeFalse();
        settings.RememberServer(new Uri("https://192.168.1.10:7360/")).ShouldBeTrue();

        settings.ServerAddresses.ShouldBe(["https://192.168.1.10:7360/", "https://hc360-srv:7360/"]);
    }

    [Fact]
    public void Tries_remembered_addresses_then_this_computer_then_servers_that_announce_the_pinned_ca()
    {
        var thisComputer = new DiscoveredServer("This computer", "WARD-PC", 7360, [IPAddress.Loopback], null, null, null, OnThisComputer: true);
        var hospital = new DiscoveredServer("NH._hc360._tcp.local", "HC360-SRV2", 7360, [IPAddress.Parse("192.168.1.20")], "1.0", "NH", Fingerprint);
        var stranger = new DiscoveredServer("XX._hc360._tcp.local", "OTHER", 7360, [IPAddress.Parse("192.168.1.30")], "1.0", "XX", new string('B', 64));

        var candidates = ServerFallback.Candidates(
            new Uri("https://hc360-srv:7360/"),
            ["https://HC360-SRV:7360/", "https://192.168.1.10:7360/", "not an address", "http://192.168.1.11:7360/", "https://192.168.1.10:7360/"],
            thisComputer,
            [stranger, hospital],
            Fingerprint);

        candidates.Select(c => c.AbsoluteUri).ShouldBe(
        [
            "https://192.168.1.10:7360/",
            "https://ward-pc:7360/",
            "https://localhost:7360/",
            "https://hc360-srv2:7360/",
            "https://hc360-srv2.local:7360/",
            "https://192.168.1.20:7360/",
        ]);
    }

    /// <summary>
    /// After the hospital CA is rolled over the server announces the new CA, and the one it replaced, which still vouches for
    /// it: a workstation that pinned the old CA finds the server by that. Another server's announcing it changes nothing: an
    /// address is used only once the server answers there over the pinned connection.
    /// </summary>
    [Fact]
    public void Tries_a_server_that_announces_the_pinned_ca_as_the_one_its_ca_replaced()
    {
        var rolled = new DiscoveredServer("NH._hc360._tcp.local", "HC360-SRV2", 7360, [IPAddress.Parse("192.168.1.20")], "1.0", "NH", new string('C', 64),
            PreviousCaFingerprint: Fingerprint);
        var stranger = new DiscoveredServer("XX._hc360._tcp.local", "OTHER", 7360, [IPAddress.Parse("192.168.1.30")], "1.0", "XX", new string('B', 64),
            PreviousCaFingerprint: new string('D', 64));

        ServerFallback.Candidates(new Uri("https://hc360-srv:7360/"), [], null, [stranger, rolled], Fingerprint).Select(c => c.AbsoluteUri).ShouldBe(
        [
            "https://hc360-srv2:7360/",
            "https://hc360-srv2.local:7360/",
            "https://192.168.1.20:7360/",
        ]);
    }

    [Fact]
    public void Has_nothing_to_try_when_nothing_else_is_known() =>
        ServerFallback.Candidates(new Uri("https://hc360-srv:7360/"), ["https://hc360-srv:7360/"], null, [], Fingerprint).ShouldBeEmpty();

    [Fact]
    public async Task Uses_the_first_address_where_the_hospital_server_answers()
    {
        using var ca = ServerTrustTests.CreateCa("CN=Nana Hospitals HC360 CA");
        using var otherCa = ServerTrustTests.CreateCa("CN=Another CA");
        using var hospital = ServerTrustTests.UsableForTls(ServerTrustTests.CreateServerCertificate(ca, "localhost", IPAddress.Loopback));
        using var stranger = ServerTrustTests.UsableForTls(ServerTrustTests.CreateServerCertificate(otherCa, "localhost", IPAddress.Loopback));

        // Nothing listens on the first address, and a machine that is not the hospital server answers on the second.
        var nothing = new TcpListener(IPAddress.Loopback, 0);
        nothing.Start();
        var nothingThere = Address(nothing);
        nothing.Stop();
        var strangerListener = new TcpListener(IPAddress.Loopback, 0);
        var hospitalListener = new TcpListener(IPAddress.Loopback, 0);
        strangerListener.Start();
        hospitalListener.Start();
        try
        {
            var serving = Task.WhenAll(ServeHealthAsync(strangerListener, stranger), ServeHealthAsync(hospitalListener, hospital));

            var found = await ServerFallback.FirstAnsweringAsync(
                [nothingThere, Address(strangerListener), Address(hospitalListener)], ca.ExportCertificatePem(), TestContext.Current.CancellationToken);

            found.ShouldBe(Address(hospitalListener));
            await serving;
        }
        finally
        {
            strangerListener.Stop();
            hospitalListener.Stop();
        }
    }

    [Fact]
    public async Task Finds_nothing_when_only_other_machines_answer()
    {
        using var ca = ServerTrustTests.CreateCa("CN=Nana Hospitals HC360 CA");
        using var otherCa = ServerTrustTests.CreateCa("CN=Another CA");
        using var stranger = ServerTrustTests.UsableForTls(ServerTrustTests.CreateServerCertificate(otherCa, "localhost", IPAddress.Loopback));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var serving = ServeHealthAsync(listener, stranger);

            (await ServerFallback.FirstAnsweringAsync([Address(listener)], ca.ExportCertificatePem(), TestContext.Current.CancellationToken))
                .ShouldBeNull();
            await serving;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static Uri Address(TcpListener listener) => new($"https://localhost:{((IPEndPoint)listener.LocalEndpoint).Port}/");

    /// <summary>
    /// Accepts one connection, offers <paramref name="certificate"/>, and answers its request as the server's liveness
    /// check does, unless the client refuses the certificate.
    /// </summary>
    private static async Task ServeHealthAsync(TcpListener listener, X509Certificate2 certificate)
    {
        using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await using var tls = new SslStream(client.GetStream());
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, TestContext.Current.CancellationToken);
            var request = new StringBuilder();
            var buffer = new byte[1024];
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await tls.ReadAsync(buffer, TestContext.Current.CancellationToken);
                if (read == 0)
                {
                    return;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException)
        {
            // The client refused the certificate and closed the handshake.
        }
    }
}
