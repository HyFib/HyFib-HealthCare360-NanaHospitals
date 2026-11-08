using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Devices;
using HyFib.HealthCare360.Platform.Approvals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Analyser connections authenticate the device before a message is read: a shared secret's answer to a fresh
/// challenge, or a client certificate (mutual TLS). A device the connection does not know is refused and listed; the lab
/// adds it, and someone other than who added it approves it; a revoked one is refused again. The gate is driven over a
/// real loopback TCP connection, as the analyser listener drives it.
/// </summary>
public sealed class LabDeviceAuthenticationTests(HospitalServer server)
{
    private const string Devices = "/api/v1/integrations/lab/devices";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_shared_secret_device_is_refused_until_added_and_approved_by_someone_else()
    {
        var code = "SS" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var connection = new LabInstrumentSettings { Code = code, Transport = LabTransport.Tcp, Port = 5100, DeviceAuthentication = LabDeviceAuthentication.SharedSecret };
        var gate = server.Services.GetRequiredService<LabDeviceGate>();
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var checker = await server.SignInAsync(HospitalServer.Users.Admin);

        // An unknown device is refused, and listed with the key id it named.
        (await SharedSecretAsync(gate, connection, "dev-unknown", "not-the-secret")).ShouldBe(LabDeviceAdmission.Unknown);
        var seen = (await maker.GetAsync<List<LabDeviceDto>>($"{Devices}?connection={code}")).ShouldHaveSingleItem();
        (seen.Status, seen.Identifier, seen.RefusedAttempts).ShouldBe((LabDeviceStatus.Seen, "dev-unknown", 1));

        // Added by one administrator: a key id and a secret, shown once; refused until approved.
        using var response = await maker.SendRawAsync(HttpMethod.Post, Devices,
            new AddLabDeviceRequest(code, "Middleware box", LabDeviceAuthentication.SharedSecret, null, null, "The analyser's middleware box in the lab."));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var added = (await response.Content.ReadFromJsonAsync<LabDeviceAdded>(ApiClient.Json, Ct))!;
        added.Secret.ShouldNotBeNullOrEmpty();
        (await SharedSecretAsync(gate, connection, added.KeyId!, added.Secret!)).ShouldBe(LabDeviceAdmission.NotApproved);

        // Not by the one who added it; by another.
        var self = await RefusedCodeAsync(maker, $"/api/v1/approvals/{added.Approval.Id}/approve", new ApproveRequest(null, added.Approval.Version));
        self.ShouldBe("approval.self");
        await checker.PostAsync($"/api/v1/approvals/{added.Approval.Id}/approve", new ApproveRequest(null, added.Approval.Version));

        // Now the right answer is admitted, and a wrong one, or another device's, is not.
        (await SharedSecretAsync(gate, connection, added.KeyId!, added.Secret!)).ShouldBe(LabDeviceAdmission.Admitted);
        (await SharedSecretAsync(gate, connection, added.KeyId!, "a-guessed-secret")).ShouldBe(LabDeviceAdmission.Failed);
        var listed = (await maker.GetAsync<List<LabDeviceDto>>($"{Devices}?connection={code}")).Single(d => d.Identifier == added.KeyId);
        (listed.Status, listed.AddedByName is not null, listed.ApprovedByName is not null).ShouldBe((LabDeviceStatus.Approved, true, true));

        // Revoked at once by anyone who sets up connections.
        await maker.PostAsync($"{Devices}/{listed.Id}/revoke", new RevokeLabDeviceRequest(listed.Version, "The middleware box was replaced."));
        (await SharedSecretAsync(gate, connection, added.KeyId!, added.Secret!)).ShouldBe(LabDeviceAdmission.NotApproved);

        // Front office may not see or add devices.
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        using var refused = await frontOffice.SendRawAsync(HttpMethod.Get, Devices);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_client_certificate_is_admitted_only_once_its_fingerprint_is_approved()
    {
        var code = "TLS" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var connection = new LabInstrumentSettings { Code = code, Transport = LabTransport.Tcp, Port = 5101, DeviceAuthentication = LabDeviceAuthentication.MutualTls };
        using var serverCertificate = Certificate("CN=hc360-gateway.test", serverAuth: true);
        using var device = Certificate("CN=analyser-xn1000", serverAuth: false);
        using var stranger = Certificate("CN=laptop", serverAuth: false);
        var gate = new LabDeviceGate(server.Services.GetRequiredService<IServiceScopeFactory>(), () => serverCertificate, TimeProvider.System, NullLogger<LabDeviceGate>.Instance);
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var checker = await server.SignInAsync(HospitalServer.Users.Admin);

        (await MutualTlsAsync(gate, connection, device)).ShouldBe(LabDeviceAdmission.Unknown);
        (await MutualTlsAsync(gate, connection, null)).ShouldBe(LabDeviceAdmission.Failed, "no client certificate");
        var seen = (await maker.GetAsync<List<LabDeviceDto>>($"{Devices}?connection={code}")).ShouldHaveSingleItem();
        seen.Identifier.ShouldBe(Convert.ToHexString(SHA256.HashData(device.RawData)));
        seen.Subject.ShouldBe("CN=analyser-xn1000");

        using var response = await maker.SendRawAsync(HttpMethod.Post, Devices,
            new AddLabDeviceRequest(code, "Haematology analyser", LabDeviceAuthentication.MutualTls, null, seen.Id, "Its certificate, issued by the lab's CA."));
        var added = (await response.Content.ReadFromJsonAsync<LabDeviceAdded>(ApiClient.Json, Ct))!;
        added.Secret.ShouldBeNull();
        await checker.PostAsync($"/api/v1/approvals/{added.Approval.Id}/approve", new ApproveRequest(null, added.Approval.Version));

        (await MutualTlsAsync(gate, connection, device)).ShouldBe(LabDeviceAdmission.Admitted);
        (await MutualTlsAsync(gate, connection, stranger)).ShouldBe(LabDeviceAdmission.Unknown);
    }

    [Fact]
    public async Task A_lab_agent_relays_an_analyser_only_once_approved_for_its_connection()
    {
        var code = "AG" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var gate = server.Services.GetRequiredService<LabDeviceGate>();
        var agent = Guid.NewGuid();
        (await gate.AdmitAgentAsync(code, agent, IPAddress.Loopback, Ct)).ShouldBe(LabDeviceAdmission.Unknown);

        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var seen = (await maker.GetAsync<List<LabDeviceDto>>($"{Devices}?connection={code}")).ShouldHaveSingleItem();
        seen.Method.ShouldBe(LabDeviceAuthentication.LabAgent);
        using var response = await maker.SendRawAsync(HttpMethod.Post, Devices,
            new AddLabDeviceRequest(code, "Lab PC 2", LabDeviceAuthentication.LabAgent, null, seen.Id, "The agent on the biochemistry PC."));
        var added = (await response.Content.ReadFromJsonAsync<LabDeviceAdded>(ApiClient.Json, Ct))!;
        (await gate.AdmitAgentAsync(code, agent, IPAddress.Loopback, Ct)).ShouldBe(LabDeviceAdmission.NotApproved);
        await (await server.SignInAsync(HospitalServer.Users.Admin)).PostAsync($"/api/v1/approvals/{added.Approval.Id}/approve", new ApproveRequest(null, added.Approval.Version));
        (await gate.AdmitAgentAsync(code, agent, IPAddress.Loopback, Ct)).ShouldBe(LabDeviceAdmission.Admitted);
        (await gate.AdmitAgentAsync(code, Guid.NewGuid(), IPAddress.Loopback, Ct)).ShouldBe(LabDeviceAdmission.Unknown);
    }

    /// <summary>A device answering the gateway's challenge over loopback TCP with the key id and secret given.</summary>
    private static async Task<LabDeviceAdmission> SharedSecretAsync(LabDeviceGate gate, LabInstrumentSettings connection, string keyId, string secret)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        using var accepted = await listener.AcceptTcpClientAsync(Ct);
        var admitting = gate.AdmitAsync(connection, accepted.GetStream(), IPAddress.Loopback, Ct);

        var device = client.GetStream();
        var challenge = await ReadLineAsync(device);
        challenge.ShouldStartWith(LabDeviceGate.ChallengePrefix);
        var answer = LabDeviceGate.Answer(secret, challenge[LabDeviceGate.ChallengePrefix.Length..]);
        await device.WriteAsync(Encoding.ASCII.GetBytes($"{LabDeviceGate.AnswerPrefix}{keyId} {answer}\r\n"), Ct);
        var (stream, admission) = await admitting;
        if (stream is not null)
        {
            (await ReadLineAsync(device)).ShouldBe("HC360-OK");
        }

        return admission;
    }

    private static async Task<LabDeviceAdmission> MutualTlsAsync(LabDeviceGate gate, LabInstrumentSettings connection, X509Certificate2? certificate)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Ct);
        using var accepted = await listener.AcceptTcpClientAsync(Ct);
        var admitting = gate.AdmitAsync(connection, accepted.GetStream(), IPAddress.Loopback, Ct);
        await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "hc360-gateway.test",
                ClientCertificates = certificate is null ? null : [certificate],
                LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate!,
            }, Ct);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
        {
            // The gateway may close the connection during the handshake.
        }

        var (stream, admission) = await admitting;
        if (stream is not null)
        {
            await stream.DisposeAsync();
        }

        return admission;
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        var line = new StringBuilder();
        var one = new byte[1];
        while (await stream.ReadAsync(one, Ct) == 1 && one[0] != '\n')
        {
            line.Append((char)one[0]);
        }

        return line.ToString().TrimEnd('\r');
    }

    private static X509Certificate2 Certificate(string subject, bool serverAuth)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [serverAuth ? new Oid("1.3.6.1.5.5.7.3.1") : new Oid("1.3.6.1.5.5.7.3.2")], false));
        if (serverAuth)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("hc360-gateway.test");
            request.CertificateExtensions.Add(names.Build());
        }

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>The refusal's code, whatever its status (an approval by the one who asked is 403 approval.self).</summary>
    private static async Task<string?> RefusedCodeAsync(ApiClient client, string path, object body)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        ((int)response.StatusCode).ShouldBeGreaterThanOrEqualTo(400);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

}
